use std::io::Write;

use crate::{
    internal::ppmd7::{PPMd7, RangeEncoder7a},
    Error, PPMD7_MAX_MEM_SIZE, PPMD7_MAX_ORDER, PPMD7_MIN_MEM_SIZE, PPMD7_MIN_ORDER, SYM_END,
};

pub struct Ppmd7aEncoder<W: Write> {
    ppmd: PPMd7<RangeEncoder7a<W>>,
}

unsafe impl<W: Write> Send for Ppmd7aEncoder<W> {}
unsafe impl<W: Write> Sync for Ppmd7aEncoder<W> {}

impl<W: Write> Ppmd7aEncoder<W> {
    pub fn new(writer: W, order: u32, mem_size: u32) -> crate::Result<Self> {
        if !(PPMD7_MIN_ORDER..=PPMD7_MAX_ORDER).contains(&order)
            || !(PPMD7_MIN_MEM_SIZE..=PPMD7_MAX_MEM_SIZE).contains(&mem_size)
        {
            return Err(Error::InvalidParameter);
        }

        let ppmd = PPMd7::new_encoder_7a(writer, order, mem_size)?;

        Ok(Self { ppmd })
    }

    pub fn get_ref(&self) -> &W {
        self.ppmd.get_ref()
    }

    pub fn get_mut(&mut self) -> &mut W {
        self.ppmd.get_mut()
    }

    pub fn into_inner(self) -> W {
        self.ppmd.into_inner()
    }

    pub fn finish(mut self, with_end_marker: bool) -> std::io::Result<W> {
        if with_end_marker {
            self.ppmd.encode_symbol(SYM_END)?;
        }
        self.flush()?;
        Ok(self.into_inner())
    }
}

impl<W: Write> Write for Ppmd7aEncoder<W> {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }

        for &byte in buf.iter() {
            self.ppmd.encode_symbol(byte as i32)?;
        }

        Ok(buf.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        self.ppmd.flush_range_encoder()
    }
}
