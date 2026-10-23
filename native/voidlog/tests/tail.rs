use std::io::Write;
use voidlog::{Next, Tail};

const MARKERS: &[&str] = &["[FLog::Output] ! Joining game", "[FLog::Network] serverId:", "\"type\":\"game_loaded\"", "[ExpChat/mountClientApp (Trace)] - Player "];

struct Random(u64);

impl Random {
    fn next(&mut self) -> u64 {
        self.0 ^= self.0 << 13;
        self.0 ^= self.0 >> 7;
        self.0 ^= self.0 << 17;
        self.0
    }

    fn below(&mut self, limit: u64) -> u64 {
        self.next() % limit
    }
}

fn reference(data: &[u8]) -> Vec<Vec<u8>> {
    let mut lines = Vec::new();
    let mut current = Vec::new();
    let mut index = 0;
    while index < data.len() {
        let byte = data[index];
        if byte == b'\n' || byte == b'\r' {
            lines.push(std::mem::take(&mut current));
            if byte == b'\r' && data.get(index + 1) == Some(&b'\n') {
                index += 1;
            }
        } else {
            current.push(byte);
        }
        index += 1;
    }
    lines
        .into_iter()
        .filter(|line| MARKERS.iter().any(|marker| line.windows(marker.len()).any(|window| window == marker.as_bytes())))
        .collect()
}

fn generate(random: &mut Random, lines: usize) -> Vec<u8> {
    let mut data = Vec::new();
    for _ in 0..lines {
        let length = match random.below(20) {
            0 => 70_000 + random.below(70_000) as usize,
            1 => 0,
            _ => random.below(300) as usize,
        };
        let alphabet: &[u8] = b" abcdefghij[]:\"0123456789\xc3\xa9";
        let mut line: Vec<u8> = (0..length).map(|_| alphabet[random.below(alphabet.len() as u64) as usize]).collect();
        if random.below(4) == 0 {
            let marker = MARKERS[random.below(MARKERS.len() as u64) as usize].as_bytes();
            let at = random.below(line.len() as u64 + 1) as usize;
            line.splice(at..at, marker.iter().copied());
        }
        data.extend_from_slice(&line);
        data.extend_from_slice(match random.below(10) {
            0 => b"\r\n",
            1 => b"\r",
            _ => b"\n",
        });
    }
    data
}

fn drain(tail: &mut Tail, found: &mut Vec<Vec<u8>>) {
    loop {
        match tail.poll().unwrap() {
            Next::Line(line) => found.push(line),
            Next::Idle => return,
            Next::Restarted => panic!("unexpected restart"),
        }
    }
}

#[test]
fn matches_reference_with_live_appends() {
    let directory = std::env::temp_dir().join(format!("voidlog-test-{}", std::process::id()));
    std::fs::create_dir_all(&directory).unwrap();
    for seed in 1..=40u64 {
        let mut random = Random(seed * 0x9E37_79B9_7F4A_7C15);
        let data = generate(&mut random, 400);
        let path = directory.join(format!("log-{seed}.log"));
        let mut file = std::fs::File::create(&path).unwrap();
        let mut tail = Tail::open(path.to_str().unwrap(), MARKERS).unwrap();
        let mut found = Vec::new();
        let mut written = 0;
        while written < data.len() {
            let step = 1 + random.below(90_000) as usize;
            let end = (written + step).min(data.len());
            file.write_all(&data[written..end]).unwrap();
            file.flush().unwrap();
            written = end;
            drain(&mut tail, &mut found);
        }
        let expected = reference(&data);
        assert_eq!(found.len(), expected.len(), "seed {seed}");
        assert!(found == expected, "seed {seed}");
    }
    std::fs::remove_dir_all(&directory).unwrap();
}

#[test]
fn waits_for_line_end_and_restarts_after_truncation() {
    let path = std::env::temp_dir().join(format!("voidlog-restart-{}.log", std::process::id()));
    let mut file = std::fs::File::create(&path).unwrap();
    let mut tail = Tail::open(path.to_str().unwrap(), MARKERS).unwrap();
    file.write_all(b"noise\n[FLog::Network] serverId: abc").unwrap();
    let mut found = Vec::new();
    drain(&mut tail, &mut found);
    assert!(found.is_empty());
    file.write_all(b"-def\n").unwrap();
    drain(&mut tail, &mut found);
    assert_eq!(found, vec![b"[FLog::Network] serverId: abc-def".to_vec()]);
    drop(file);
    let mut file = std::fs::File::create(&path).unwrap();
    file.write_all(b"\"type\":\"game_loaded\"\n").unwrap();
    assert!(matches!(tail.poll().unwrap(), Next::Restarted));
    let mut after = Vec::new();
    drain(&mut tail, &mut after);
    assert_eq!(after, vec![b"\"type\":\"game_loaded\"".to_vec()]);
    drop(tail);
    drop(file);
    std::fs::remove_file(&path).unwrap();
}
