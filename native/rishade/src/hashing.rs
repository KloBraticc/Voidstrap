use sha2::{Digest, Sha256};
use std::io::{self, Read};

pub(crate) fn sha256_bytes(bytes: impl AsRef<[u8]>) -> String {
    encode_hex(&Sha256::digest(bytes))
}

pub(crate) fn sha256_reader(mut reader: impl Read) -> io::Result<String> {
    let mut hasher = Sha256::new();
    let mut buffer = [0u8; 65536];
    loop {
        let count = match reader.read(&mut buffer) {
            Ok(count) => count,
            Err(error) if error.kind() == io::ErrorKind::Interrupted => continue,
            Err(error) => return Err(error),
        };
        if count == 0 {
            return Ok(encode_hex(&hasher.finalize()));
        }
        hasher.update(&buffer[..count]);
    }
}

fn encode_hex(bytes: &[u8]) -> String {
    const DIGITS: &[u8; 16] = b"0123456789abcdef";
    bytes
        .iter()
        .flat_map(|byte| {
            [
                char::from(DIGITS[(byte >> 4) as usize]),
                char::from(DIGITS[(byte & 15) as usize]),
            ]
        })
        .collect()
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hashes_bytes_to_standard_lowercase_sha256() {
        assert_eq!(
            sha256_bytes(b"abc"),
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad"
        );
        assert_eq!(
            sha256_bytes(b""),
            "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"
        );
    }

    #[test]
    fn hashes_streams_across_multiple_read_buffers() {
        let input = std::io::Cursor::new(vec![b'a'; 1_000_000]);
        assert_eq!(
            sha256_reader(input).unwrap(),
            "cdc76e5c9914fb9281a1c7e284d73e67f1809a48a497200e046d39ccc7112cd0"
        );
    }

    #[test]
    fn retries_interrupted_reads() {
        struct InterruptedReader(bool, std::io::Cursor<Vec<u8>>);
        impl Read for InterruptedReader {
            fn read(&mut self, buffer: &mut [u8]) -> io::Result<usize> {
                if !self.0 {
                    self.0 = true;
                    return Err(io::ErrorKind::Interrupted.into());
                }
                self.1.read(buffer)
            }
        }
        let input = InterruptedReader(false, std::io::Cursor::new(b"abc".to_vec()));
        assert_eq!(sha256_reader(input).unwrap(), sha256_bytes(b"abc"));
    }
}
