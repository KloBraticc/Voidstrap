use crate::ts::{self, PACKET};
use std::collections::VecDeque;
use std::fs::File;
use std::io::{BufWriter, Write};
use std::time::{Duration, Instant};

pub struct Segment {
    pub start: Instant,
    pub data: Vec<u8>,
}

struct ActiveRecorder {
    file: BufWriter<File>,
    path: String,
    started: Instant,
    bytes: u64,
    seeded: bool,
}

pub struct Ring {
    segments: VecDeque<Segment>,
    pat: Option<Vec<u8>>,
    pmt: Option<Vec<u8>>,
    pmt_pid: Option<u16>,
    video_pid: Option<u16>,
    bytes: usize,
    keep: Duration,
    max_bytes: usize,
    pending: Vec<u8>,
    first_video: Option<Instant>,
    saw_key: bool,
    last_size: usize,
    recorder: Option<ActiveRecorder>,
}

impl Ring {
    pub fn new(keep: Duration, max_bytes: usize) -> Self {
        Ring {
            segments: VecDeque::new(),
            pat: None,
            pmt: None,
            pmt_pid: None,
            video_pid: None,
            bytes: 0,
            keep,
            max_bytes,
            pending: Vec::with_capacity(PACKET * 512),
            first_video: None,
            saw_key: false,
            last_size: 256 * 1024,
            recorder: None,
        }
    }

    pub fn clear(&mut self) {
        let keep = self.keep;
        let max = self.max_bytes;
        let mut rec = self.recorder.take();
        if let Some(r) = rec.as_mut() {
            r.seeded = false;
        }
        *self = Ring::new(keep, max);
        self.recorder = rec;
    }

    pub fn start_recording(&mut self, temp_path: &str) -> Result<(), String> {
        if let Some(parent) = std::path::Path::new(temp_path).parent() {
            let _ = std::fs::create_dir_all(parent);
        }
        let file = File::create(temp_path)
            .map_err(|e| format!("failed to create recording file: {e}"))?;
        let mut writer = BufWriter::with_capacity(256 * 1024, file);
        let mut written = 0u64;
        let mut seeded = false;

        if let (Some(pat), Some(pmt), Some(seg)) = (
            self.pat.as_ref(),
            self.pmt.as_ref(),
            self.segments.back(),
        ) {
            writer
                .write_all(pat)
                .and_then(|_| writer.write_all(pmt))
                .and_then(|_| writer.write_all(&seg.data))
                .map_err(|e| format!("failed to write recording header: {e}"))?;
            written = (pat.len() + pmt.len() + seg.data.len()) as u64;
            seeded = true;
        }

        self.recorder = Some(ActiveRecorder {
            file: writer,
            path: temp_path.to_owned(),
            started: Instant::now(),
            bytes: written,
            seeded,
        });
        Ok(())
    }

    pub fn stop_recording(&mut self) -> Result<(String, u64), String> {
        let mut rec = self
            .recorder
            .take()
            .ok_or_else(|| "No active recording".to_string())?;

        if !rec.seeded {
            if let (Some(pat), Some(pmt), Some(seg)) = (
                self.pat.as_ref(),
                self.pmt.as_ref(),
                self.segments.back(),
            ) {
                let _ = rec.file.write_all(pat);
                let _ = rec.file.write_all(pmt);
                let _ = rec.file.write_all(&seg.data);
                rec.bytes += (pat.len() + pmt.len() + seg.data.len()) as u64;
                rec.seeded = true;
            }
        }

        rec.file
            .flush()
            .map_err(|e| format!("failed to flush recording: {e}"))?;
        drop(rec.file);
        Ok((rec.path, rec.bytes))
    }

    pub fn is_recording(&self) -> bool {
        self.recorder.is_some()
    }

    pub fn recording_info(&self) -> (bool, u64, u64) {
        match self.recorder.as_ref() {
            Some(r) => (true, r.started.elapsed().as_millis() as u64, r.bytes),
            None => (false, 0, 0),
        }
    }

    pub fn feed(&mut self, data: &[u8]) {
        let now = Instant::now();
        self.pending.extend_from_slice(data);
        let mut offset = 0;
        let mut packet = [0u8; PACKET];
        while self.pending.len() - offset >= PACKET {
            if self.pending[offset] != 0x47 {
                offset += 1;
                continue;
            }
            packet.copy_from_slice(&self.pending[offset..offset + PACKET]);
            self.packet(&packet, now);
            offset += PACKET;
        }
        self.pending.drain(..offset);
        self.evict(now);
    }

    fn packet(&mut self, p: &[u8; PACKET], now: Instant) {
        let pid = ts::pid(p);
        if pid == 0 {
            if let Some(pmt) = ts::pmt_pid(p) {
                self.pmt_pid = Some(pmt);
                self.pat = Some(p.to_vec());
            }
            return;
        }
        if Some(pid) == self.pmt_pid {
            if let Some(video) = ts::video_pid(p) {
                self.video_pid = Some(video);
                self.pmt = Some(p.to_vec());
            }
            return;
        }
        let video = Some(pid) == self.video_pid;
        if video && self.first_video.is_none() {
            self.first_video = Some(now);
        }
        let key = video && ts::unit_start(p) && ts::random_access(p);
        if key {
            self.saw_key = true;
        }
        let timed = !self.saw_key
            && video
            && ts::unit_start(p)
            && self.first_video.map_or(false, |t| now.duration_since(t) > Duration::from_secs(3))
            && self.segments.back().map_or(true, |s| now.duration_since(s.start) >= Duration::from_secs(1));
        if key || timed {
            if let Some(last) = self.segments.back() {
                self.last_size = last.data.len().max(PACKET * 64);
            }
            self.segments.push_back(Segment {
                start: now,
                data: Vec::with_capacity(self.last_size + self.last_size / 4),
            });
        }
        if let Some(segment) = self.segments.back_mut() {
            segment.data.extend_from_slice(p);
            self.bytes += PACKET;
        }
        if let Some(rec) = self.recorder.as_mut() {
            if !rec.seeded {
                if let (Some(pat), Some(pmt), Some(seg)) = (
                    self.pat.as_ref(),
                    self.pmt.as_ref(),
                    self.segments.back(),
                ) {
                    if rec.file.write_all(pat).is_ok()
                        && rec.file.write_all(pmt).is_ok()
                        && rec.file.write_all(&seg.data).is_ok()
                    {
                        rec.bytes += (pat.len() + pmt.len() + seg.data.len()) as u64;
                        rec.seeded = true;
                    }
                }
            } else if !self.segments.is_empty() && rec.file.write_all(p).is_ok() {
                rec.bytes += PACKET as u64;
            }
        }
    }

    fn evict(&mut self, now: Instant) {
        while self.segments.len() > 1 {
            let old = now.duration_since(self.segments[1].start) >= self.keep;
            if old || self.bytes > self.max_bytes {
                if let Some(segment) = self.segments.pop_front() {
                    self.bytes -= segment.data.len();
                }
            } else {
                break;
            }
        }
    }

    pub fn has_video(&self) -> bool {
        !self.segments.is_empty() && self.pat.is_some() && self.pmt.is_some()
    }

    pub fn buffered(&self) -> Duration {
        self.segments
            .front()
            .map_or(Duration::ZERO, |s| Instant::now().duration_since(s.start))
    }

    pub fn bytes(&self) -> usize {
        self.bytes
    }

    pub fn snapshot(&self, seconds: u32) -> Option<Vec<u8>> {
        let pat = self.pat.as_ref()?;
        let pmt = self.pmt.as_ref()?;
        if self.segments.is_empty() {
            return None;
        }
        let mut first = 0;
        if seconds > 0 {
            if let Some(cutoff) = Instant::now().checked_sub(Duration::from_secs(seconds as u64)) {
                for (i, segment) in self.segments.iter().enumerate() {
                    if segment.start <= cutoff {
                        first = i;
                    } else {
                        break;
                    }
                }
            }
        }
        let total: usize = self.segments.iter().skip(first).map(|s| s.data.len()).sum();
        let mut out = Vec::with_capacity(total + PACKET * 2);
        out.extend_from_slice(pat);
        out.extend_from_slice(pmt);
        for segment in self.segments.iter().skip(first) {
            out.extend_from_slice(&segment.data);
        }
        Some(out)
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn make_pat(pmt_pid: u16) -> [u8; PACKET] {
        let mut p = [0xffu8; PACKET];
        p[0] = 0x47;
        p[1] = 0x40;
        p[2] = 0x00;
        p[3] = 0x10;
        p[4] = 0x00;
        p[5] = 0x00;
        p[6] = 0xb0;
        p[7] = 13;
        p[8] = 0x00;
        p[9] = 0x01;
        p[10] = 0xc1;
        p[11] = 0x00;
        p[12] = 0x00;
        p[13] = 0x00;
        p[14] = 0x01;
        p[15] = 0xe0 | ((pmt_pid >> 8) as u8 & 0x1f);
        p[16] = (pmt_pid & 0xff) as u8;
        p
    }

    fn make_pmt(pmt_pid: u16, video_pid: u16) -> [u8; PACKET] {
        let mut p = [0xffu8; PACKET];
        p[0] = 0x47;
        p[1] = 0x40 | ((pmt_pid >> 8) as u8 & 0x1f);
        p[2] = (pmt_pid & 0xff) as u8;
        p[3] = 0x10;
        p[4] = 0x00;
        p[5] = 0x02;
        p[6] = 0xb0;
        p[7] = 18;
        p[8] = 0x00;
        p[9] = 0x01;
        p[10] = 0xc1;
        p[11] = 0x00;
        p[12] = 0x00;
        p[13] = 0xe1;
        p[14] = 0x00;
        p[15] = 0xf0;
        p[16] = 0x00;
        p[17] = 0x1b;
        p[18] = 0xe0 | ((video_pid >> 8) as u8 & 0x1f);
        p[19] = (video_pid & 0xff) as u8;
        p[20] = 0xf0;
        p[21] = 0x00;
        p
    }

    fn make_video(video_pid: u16, keyframe: bool) -> [u8; PACKET] {
        let mut p = [0x11u8; PACKET];
        p[0] = 0x47;
        p[1] = (if keyframe { 0x40 } else { 0x00 }) | ((video_pid >> 8) as u8 & 0x1f);
        p[2] = (video_pid & 0xff) as u8;
        if keyframe {
            p[3] = 0x30;
            p[4] = 7;
            p[5] = 0x40;
        } else {
            p[3] = 0x10;
        }
        p
    }

    #[test]
    fn parses_tables_and_snapshots_gop_segments() {
        let mut ring = Ring::new(Duration::from_secs(30), 16 * 1024 * 1024);
        assert!(!ring.has_video());

        ring.feed(&make_pat(0x1000));
        ring.feed(&make_pmt(0x1000, 0x0100));
        ring.feed(&make_video(0x0100, true));
        ring.feed(&make_video(0x0100, false));
        ring.feed(&make_video(0x0100, true));

        assert!(ring.has_video());
        assert_eq!(ring.bytes(), PACKET * 3);

        let snap = ring.snapshot(30).expect("snapshot should succeed");
        assert_eq!(snap.len(), PACKET * 5);
        assert_eq!(ts::pid(&snap[0..PACKET]), 0);
        assert_eq!(ts::pid(&snap[PACKET..PACKET * 2]), 0x1000);
        assert_eq!(ts::pid(&snap[PACKET * 2..PACKET * 3]), 0x0100);
    }

    #[test]
    fn evicts_when_max_bytes_exceeded() {
        let mut ring = Ring::new(Duration::from_secs(60), PACKET * 3);
        ring.feed(&make_pat(0x1000));
        ring.feed(&make_pmt(0x1000, 0x0100));
        ring.feed(&make_video(0x0100, true));
        ring.feed(&make_video(0x0100, false));
        ring.feed(&make_video(0x0100, true));
        ring.feed(&make_video(0x0100, false));
        assert!(ring.bytes() <= PACKET * 3);
        assert!(ring.has_video());
    }
}
