use std::io::Write;

use crate::{
    internal::ppmd8::{PPMd8, RangeEncoder},
    Error, RestoreMethod, PPMD8_MAX_MEM_SIZE, PPMD8_MAX_ORDER, PPMD8_MIN_MEM_SIZE, PPMD8_MIN_ORDER,
    SYM_END,
};

pub struct Ppmd8Encoder<W: Write> {
    ppmd: PPMd8<RangeEncoder<W>>,
}

unsafe impl<W: Write> Send for Ppmd8Encoder<W> {}
unsafe impl<W: Write> Sync for Ppmd8Encoder<W> {}

impl<W: Write> Ppmd8Encoder<W> {
    pub fn new(
        writer: W,
        order: u32,
        mem_size: u32,
        restore_method: RestoreMethod,
    ) -> crate::Result<Self> {
        if !(PPMD8_MIN_ORDER..=PPMD8_MAX_ORDER).contains(&order)
            || !(PPMD8_MIN_MEM_SIZE..=PPMD8_MAX_MEM_SIZE).contains(&mem_size)
        {
            return Err(Error::InvalidParameter);
        }

        let ppmd = PPMd8::new_encoder(writer, order, mem_size, restore_method)?;

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
            unsafe { self.ppmd.encode_symbol(SYM_END)? };
        }
        self.flush()?;
        Ok(self.into_inner())
    }
}

impl<W: Write> Write for Ppmd8Encoder<W> {
    fn write(&mut self, buf: &[u8]) -> std::io::Result<usize> {
        if buf.is_empty() {
            return Ok(0);
        }

        for &byte in buf.iter() {
            unsafe { self.ppmd.encode_symbol(byte as i32)? };
        }

        Ok(buf.len())
    }

    fn flush(&mut self) -> std::io::Result<()> {
        self.ppmd.flush_range_encoder()
    }
}
