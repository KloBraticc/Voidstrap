use std::io::Read;

use crate::{
    internal::ppmd7::{PPMd7, RangeDecoder},
    Error, PPMD7_MAX_MEM_SIZE, PPMD7_MAX_ORDER, PPMD7_MIN_MEM_SIZE, PPMD7_MIN_ORDER, SYM_END,
};

pub struct Ppmd7Decoder<R: Read> {
    ppmd: PPMd7<RangeDecoder<R>>,
    finished: bool,
}

unsafe impl<R: Read> Send for Ppmd7Decoder<R> {}
unsafe impl<R: Read> Sync for Ppmd7Decoder<R> {}

impl<R: Read> Ppmd7Decoder<R> {
    pub fn new(reader: R, order: u32, mem_size: u32) -> crate::Result<Self> {
        if !(PPMD7_MIN_ORDER..=PPMD7_MAX_ORDER).contains(&order)
            || !(PPMD7_MIN_MEM_SIZE..=PPMD7_MAX_MEM_SIZE).contains(&mem_size)
        {
            return Err(Error::InvalidParameter);
        }

        let ppmd = PPMd7::new_decoder(reader, order, mem_size)?;

        Ok(Self {
            ppmd,
            finished: false,
        })
    }

    pub fn get_ref(&self) -> &R {
        self.ppmd.get_ref()
    }

    pub fn get_mut(&mut self) -> &mut R {
        self.ppmd.get_mut()
    }

    pub fn into_inner(self) -> R {
        self.ppmd.into_inner()
    }
}

impl<R: Read> Read for Ppmd7Decoder<R> {
    fn read(&mut self, buf: &mut [u8]) -> std::io::Result<usize> {
        if self.finished {
            return Ok(0);
        }

        if buf.is_empty() {
            return Ok(0);
        }

        let mut sym = 0;
        let mut decoded = 0;

        for byte in buf.iter_mut() {
            match self.ppmd.decode_symbol() {
                Ok(symbol) => sym = symbol,
                Err(err) => {
                    if err.kind() == std::io::ErrorKind::UnexpectedEof {
                        self.finished = true;
                        return Ok(decoded);
                    }
                    return Err(err);
                }
            }

            if sym < 0 {
                break;
            }

            *byte = sym as u8;
            decoded += 1;
        }

        let code = self.ppmd.range_decoder_code();

        if sym >= 0 {
            return Ok(decoded);
        }

        self.finished = true;

        if sym != SYM_END || code != 0 {
            return Err(std::io::Error::new(
                std::io::ErrorKind::InvalidData,
                "Error during PPMd decoding",
            ));
        }

        Ok(decoded)
    }
}
