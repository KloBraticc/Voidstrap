use std::io::Cursor;
use std::panic::{catch_unwind, AssertUnwindSafe};

use image::imageops::FilterType;
use image::{DynamicImage, ImageBuffer, ImageDecoder, ImageReader, Limits, Rgba, RgbaImage};

pub const VI_OK: i32 = 0;
pub const VI_INVALID: i32 = 1;
pub const VI_UNSUPPORTED: i32 = 2;
pub const VI_TOO_LARGE: i32 = 3;
pub const VI_FAILED: i32 = 4;

#[no_mangle]
pub extern "C" fn vi_version() -> u32 {
    1
}

#[no_mangle]
pub unsafe extern "C" fn vi_decode_bgra(
    data: *const u8,
    length: usize,
    max_width: u32,
    max_bytes: u64,
    out_width: *mut u32,
    out_height: *mut u32,
    out_pixels: *mut *mut u8,
    out_length: *mut usize,
) -> i32 {
    if data.is_null() || length == 0 || out_width.is_null() || out_height.is_null() || out_pixels.is_null() || out_length.is_null() {
        return VI_INVALID;
    }
    *out_pixels = std::ptr::null_mut();
    *out_length = 0;
    let input = std::slice::from_raw_parts(data, length);
    let result = catch_unwind(AssertUnwindSafe(|| decode(input, max_width, max_bytes)));
    match result {
        Ok(Ok((width, height, mut pixels))) => {
            pixels.shrink_to_fit();
            let mut boxed = pixels.into_boxed_slice();
            *out_width = width;
            *out_height = height;
            *out_length = boxed.len();
            *out_pixels = boxed.as_mut_ptr();
            std::mem::forget(boxed);
            VI_OK
        }
        Ok(Err(code)) => code,
        Err(_) => VI_FAILED,
    }
}

#[no_mangle]
pub unsafe extern "C" fn vi_free(pixels: *mut u8, length: usize) {
    if pixels.is_null() || length == 0 {
        return;
    }
    drop(Box::from_raw(std::ptr::slice_from_raw_parts_mut(pixels, length)));
}

fn decode(input: &[u8], max_width: u32, max_bytes: u64) -> Result<(u32, u32, Vec<u8>), i32> {
    let reader = ImageReader::new(Cursor::new(input)).with_guessed_format().map_err(|_| VI_INVALID)?;
    if reader.format().is_none() {
        return Err(VI_UNSUPPORTED);
    }
    let mut decoder = reader.into_decoder().map_err(|_| VI_UNSUPPORTED)?;
    let (width, height) = decoder.dimensions();
    if width == 0 || height == 0 {
        return Err(VI_INVALID);
    }
    if (width as u64) * (height as u64) * 4 > max_bytes {
        return Err(VI_TOO_LARGE);
    }
    let mut limits = Limits::default();
    limits.max_alloc = Some(max_bytes.saturating_mul(2));
    decoder.set_limits(limits).map_err(|_| VI_TOO_LARGE)?;
    let orientation = decoder.orientation().ok();
    let mut image = DynamicImage::from_decoder(decoder).map_err(|_| VI_FAILED)?;
    if let Some(orientation) = orientation {
        image.apply_orientation(orientation);
    }
    let mut rgba = image.into_rgba8();
    if max_width > 0 && rgba.width() > max_width {
        let scaled = ((rgba.height() as f64) * (max_width as f64) / (rgba.width() as f64)).round().max(1.0) as u32;
        rgba = resize_premultiplied(&rgba, max_width, scaled);
    }
    let (width, height) = rgba.dimensions();
    let mut pixels = rgba.into_raw();
    for pixel in pixels.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
    Ok((width, height, pixels))
}

fn resize_premultiplied(source: &RgbaImage, width: u32, height: u32) -> RgbaImage {
    let premultiplied: ImageBuffer<Rgba<f32>, Vec<f32>> = ImageBuffer::from_fn(source.width(), source.height(), |x, y| {
        let pixel = source.get_pixel(x, y).0;
        let alpha = pixel[3] as f32 / 255.0;
        Rgba([pixel[0] as f32 / 255.0 * alpha, pixel[1] as f32 / 255.0 * alpha, pixel[2] as f32 / 255.0 * alpha, alpha])
    });
    let resized = image::imageops::resize(&premultiplied, width, height, FilterType::CatmullRom);
    RgbaImage::from_fn(width, height, |x, y| {
        let pixel = resized.get_pixel(x, y).0;
        let alpha = pixel[3].clamp(0.0, 1.0);
        let channel = |value: f32| -> u8 {
            if alpha <= 0.0 {
                0
            } else {
                ((value / alpha).clamp(0.0, 1.0) * 255.0).round() as u8
            }
        };
        Rgba([channel(pixel[0]), channel(pixel[1]), channel(pixel[2]), (alpha * 255.0).round() as u8])
    })
}
