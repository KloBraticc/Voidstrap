#![cfg(unix)]

use std::io::{Read, Write};
use std::os::unix::net::UnixListener;
use std::time::{Duration, Instant};

fn frame(opcode: u32, data: &[u8]) -> Vec<u8> {
    let mut bytes = opcode.to_le_bytes().to_vec();
    bytes.extend_from_slice(&(data.len() as u32).to_le_bytes());
    bytes.extend_from_slice(data);
    bytes
}

fn wait_frame(connection: &voidrpc::Connection) -> voidrpc::Frame {
    let deadline = Instant::now() + Duration::from_secs(5);
    loop {
        if let Some(frame) = connection.read() {
            return frame;
        }
        assert!(Instant::now() < deadline, "no frame arrived");
        std::thread::sleep(Duration::from_millis(5));
    }
}

#[test]
fn round_trip_split_and_combined_frames() {
    let directory = std::env::temp_dir().join(format!("voidrpc-test-{}", std::process::id()));
    let _ = std::fs::remove_dir_all(&directory);
    std::fs::create_dir_all(&directory).unwrap();
    std::env::set_var("XDG_RUNTIME_DIR", &directory);
    let listener = UnixListener::bind(directory.join("discord-ipc-3")).unwrap();

    let server = std::thread::spawn(move || {
        let mut header = [0u8; 8];
        let mut socket = loop {
            let (mut socket, _) = listener.accept().unwrap();
            if socket.read_exact(&mut header).is_ok() {
                break socket;
            }
        };
        let length = u32::from_le_bytes([header[4], header[5], header[6], header[7]]) as usize;
        let mut body = vec![0u8; length];
        socket.read_exact(&mut body).unwrap();
        assert_eq!(u32::from_le_bytes([header[0], header[1], header[2], header[3]]), 0);
        assert_eq!(body, b"{\"v\":1}");
        let ready = frame(1, "{\"evt\":\"READY\",\"name\":\"\u{00e9}\"}".as_bytes());
        socket.write_all(&ready[..5]).unwrap();
        std::thread::sleep(Duration::from_millis(50));
        socket.write_all(&ready[5..]).unwrap();
        let mut combined = frame(1, b"one");
        combined.extend(frame(3, b"two"));
        socket.write_all(&combined).unwrap();
        std::thread::sleep(Duration::from_millis(200));
    });

    assert_eq!(voidrpc::voidrpc_find_pipe(), 3);
    let connection = voidrpc::Connection::open(-1).expect("connects");
    assert_eq!(connection.pipe, 3);
    assert!(connection.write(0, b"{\"v\":1}"));
    let ready = wait_frame(&connection);
    assert_eq!(ready.opcode, 1);
    assert_eq!(ready.data, "{\"evt\":\"READY\",\"name\":\"\u{00e9}\"}".as_bytes());
    assert_eq!(wait_frame(&connection).data, b"one");
    let second = wait_frame(&connection);
    assert_eq!((second.opcode, second.data.as_slice()), (3, &b"two"[..]));
    server.join().unwrap();
    let deadline = Instant::now() + Duration::from_secs(5);
    while connection.is_connected() {
        assert!(Instant::now() < deadline, "close was not noticed");
        std::thread::sleep(Duration::from_millis(5));
    }
    assert!(!connection.write(1, b"late"));
    assert!(voidrpc::Connection::open(5).is_none());
    drop(connection);
    let _ = std::fs::remove_dir_all(&directory);
}
