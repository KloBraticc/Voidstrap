use std::io::Cursor;
use std::panic::{catch_unwind, AssertUnwindSafe};

use image::imageops::FilterType;
use image::{DynamicImage, ImageDecoder, ImageReader, Limits, RgbaImage};

pub const VI_OK: i32 = 0;
pub const VI_INVALID: i32 = 1;
pub const VI_UNSUPPORTED: i32 = 2;
pub const VI_TOO_LARGE: i32 = 3;
pub const VI_FAILED: i32 = 4;

#[no_mangle]
pub extern "C" fn vi_version() -> u32 {
    5
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
    let reader = open_reader(input)?;
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
    resize_with(source, width, height, 0).unwrap_or_else(|| image::imageops::resize(source, width, height, FilterType::CatmullRom))
}

fn resize_with(source: &RgbaImage, width: u32, height: u32, filter: u32) -> Option<RgbaImage> {
    use fast_image_resize::images::Image;
    use fast_image_resize::{FilterType as Kernel, PixelType, ResizeAlg, ResizeOptions, Resizer};
    let algorithm = match filter {
        0 => ResizeAlg::Convolution(Kernel::CatmullRom),
        1 => ResizeAlg::Convolution(Kernel::Lanczos3),
        2 => ResizeAlg::Convolution(Kernel::Bilinear),
        3 => ResizeAlg::Nearest,
        _ => return None,
    };
    let input = Image::from_vec_u8(source.width(), source.height(), source.as_raw().clone(), PixelType::U8x4).ok()?;
    let mut output = Image::new(width, height, PixelType::U8x4);
    Resizer::new().resize(&input, &mut output, &ResizeOptions::new().resize_alg(algorithm).use_alpha(true)).ok()?;
    RgbaImage::from_raw(width, height, output.into_vec())
}

const MAX_EDGE: u32 = 16384;
const MAX_PIXEL_BYTES: u64 = 512 * 1024 * 1024;

unsafe fn give(mut bytes: Vec<u8>, out_bytes: *mut *mut u8, out_length: *mut usize) {
    bytes.shrink_to_fit();
    let mut boxed = bytes.into_boxed_slice();
    *out_length = boxed.len();
    *out_bytes = boxed.as_mut_ptr();
    std::mem::forget(boxed);
}

unsafe fn bgra_image(pixels: *const u8, length: usize, width: u32, height: u32) -> Option<RgbaImage> {
    if pixels.is_null() || width == 0 || height == 0 || width > MAX_EDGE || height > MAX_EDGE {
        return None;
    }
    let expected = (width as u64) * (height as u64) * 4;
    if expected != length as u64 || expected > MAX_PIXEL_BYTES {
        return None;
    }
    let mut data = std::slice::from_raw_parts(pixels, length).to_vec();
    for pixel in data.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
    RgbaImage::from_raw(width, height, data)
}

fn to_bgra(image: RgbaImage) -> Vec<u8> {
    let mut data = image.into_raw();
    for pixel in data.chunks_exact_mut(4) {
        pixel.swap(0, 2);
    }
    data
}

#[no_mangle]
pub unsafe extern "C" fn vi_resize_bgra(
    pixels: *const u8,
    length: usize,
    width: u32,
    height: u32,
    new_width: u32,
    new_height: u32,
    filter: u32,
    out_pixels: *mut *mut u8,
    out_length: *mut usize,
) -> i32 {
    if out_pixels.is_null() || out_length.is_null() || new_width == 0 || new_height == 0 || new_width > MAX_EDGE || new_height > MAX_EDGE {
        return VI_INVALID;
    }
    if (new_width as u64) * (new_height as u64) * 4 > MAX_PIXEL_BYTES {
        return VI_TOO_LARGE;
    }
    *out_pixels = std::ptr::null_mut();
    *out_length = 0;
    let Some(source) = bgra_image(pixels, length, width, height) else {
        return VI_INVALID;
    };
    if filter > 3 {
        return VI_INVALID;
    }
    match catch_unwind(AssertUnwindSafe(|| resize_with(&source, new_width, new_height, filter).map(to_bgra))) {
        Ok(None) => VI_FAILED,
        Ok(Some(data)) => {
            give(data, out_pixels, out_length);
            VI_OK
        }
        Err(_) => VI_FAILED,
    }
}

#[no_mangle]
pub unsafe extern "C" fn vi_encode_bgra(
    pixels: *const u8,
    length: usize,
    width: u32,
    height: u32,
    format: u32,
    level: u32,
    out_bytes: *mut *mut u8,
    out_length: *mut usize,
) -> i32 {
    if out_bytes.is_null() || out_length.is_null() {
        return VI_INVALID;
    }
    *out_bytes = std::ptr::null_mut();
    *out_length = 0;
    let Some(image) = bgra_image(pixels, length, width, height) else {
        return VI_INVALID;
    };
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<Vec<u8>, i32> {
        let mut output = Vec::new();
        match format {
            0 => {
                let compression = match level {
                    0 => image::codecs::png::CompressionType::Fast,
                    2 => image::codecs::png::CompressionType::Best,
                    _ => image::codecs::png::CompressionType::Default,
                };
                let encoder = image::codecs::png::PngEncoder::new_with_quality(&mut output, compression, image::codecs::png::FilterType::Adaptive);
                image.write_with_encoder(encoder).map_err(|_| VI_FAILED)?;
            }
            1 => {
                let quality = level.clamp(1, 100) as u8;
                let rgb = DynamicImage::ImageRgba8(image).into_rgb8();
                let encoder = image::codecs::jpeg::JpegEncoder::new_with_quality(&mut output, quality);
                rgb.write_with_encoder(encoder).map_err(|_| VI_FAILED)?;
            }
            2 => {
                let encoder = image::codecs::bmp::BmpEncoder::new(&mut output);
                image.write_with_encoder(encoder).map_err(|_| VI_FAILED)?;
            }
            3 => {
                let encoder = image::codecs::webp::WebPEncoder::new_lossless(&mut output);
                image.write_with_encoder(encoder).map_err(|_| VI_FAILED)?;
            }
            4 => {
                let mut encoder = image::codecs::gif::GifEncoder::new(&mut output);
                encoder.encode_frame(image::Frame::new(image)).map_err(|_| VI_FAILED)?;
            }
            _ => return Err(VI_UNSUPPORTED),
        }
        Ok(output)
    }));
    match result {
        Ok(Ok(data)) => {
            give(data, out_bytes, out_length);
            VI_OK
        }
        Ok(Err(code)) => code,
        Err(_) => VI_FAILED,
    }
}

#[no_mangle]
pub unsafe extern "C" fn vi_identify(data: *const u8, length: usize, out_width: *mut u32, out_height: *mut u32) -> i32 {
    if data.is_null() || length == 0 || out_width.is_null() || out_height.is_null() {
        return VI_INVALID;
    }
    let input = std::slice::from_raw_parts(data, length);
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<(u32, u32), i32> {
        open_reader(input)?.into_dimensions().map_err(|_| VI_UNSUPPORTED)
    }));
    match result {
        Ok(Ok((width, height))) if width > 0 && height > 0 => {
            *out_width = width;
            *out_height = height;
            VI_OK
        }
        Ok(Ok(_)) => VI_INVALID,
        Ok(Err(code)) => code,
        Err(_) => VI_FAILED,
    }
}

fn open_reader(input: &[u8]) -> Result<ImageReader<Cursor<&[u8]>>, i32> {
    let mut reader = ImageReader::new(Cursor::new(input)).with_guessed_format().map_err(|_| VI_INVALID)?;
    if reader.format().is_none() {
        if input.len() < 18 {
            return Err(VI_UNSUPPORTED);
        }
        reader.set_format(image::ImageFormat::Tga);
    }
    Ok(reader)
}

fn fit_size(width: u32, height: u32, max_width: u32, max_height: u32) -> (u32, u32) {
    if (max_width == 0 || width <= max_width) && (max_height == 0 || height <= max_height) {
        return (width, height);
    }
    let scale_x = if max_width == 0 { f64::MAX } else { max_width as f64 / width as f64 };
    let scale_y = if max_height == 0 { f64::MAX } else { max_height as f64 / height as f64 };
    let scale = scale_x.min(scale_y);
    (((width as f64 * scale).round().max(1.0)) as u32, ((height as f64 * scale).round().max(1.0)) as u32)
}

fn animation_frames(input: &[u8], format: image::ImageFormat) -> Option<image::Frames<'_>> {
    use image::AnimationDecoder;
    match format {
        image::ImageFormat::Gif => image::codecs::gif::GifDecoder::new(Cursor::new(input)).ok().map(|decoder| decoder.into_frames()),
        image::ImageFormat::WebP => {
            let decoder = image::codecs::webp::WebPDecoder::new(Cursor::new(input)).ok()?;
            if decoder.has_animation() {
                Some(decoder.into_frames())
            } else {
                None
            }
        }
        image::ImageFormat::Png => {
            let decoder = image::codecs::png::PngDecoder::new(Cursor::new(input)).ok()?;
            if decoder.is_apng().unwrap_or(false) {
                decoder.apng().ok().map(|decoder| decoder.into_frames())
            } else {
                None
            }
        }
        _ => None,
    }
}

fn decode_animation(input: &[u8], max_width: u32, max_height: u32, max_frames: u32, max_bytes: u64) -> Result<(u32, u32, Vec<u8>, Vec<u8>), i32> {
    let reader = open_reader(input)?;
    let format = reader.format().ok_or(VI_UNSUPPORTED)?;
    let (source_width, source_height) = reader.into_dimensions().map_err(|_| VI_UNSUPPORTED)?;
    if source_width == 0 || source_height == 0 || (source_width as u64) * (source_height as u64) * 4 > max_bytes {
        return Err(VI_TOO_LARGE);
    }
    let (width, height) = fit_size(source_width, source_height, max_width, max_height);
    let frame_bytes = (width as u64) * (height as u64) * 4;
    let budget = (max_bytes / frame_bytes).max(1).min(max_frames.max(1) as u64) as usize;
    let mut pixels: Vec<u8> = Vec::new();
    let mut delays: Vec<u8> = Vec::new();
    if let Some(frames) = animation_frames(input, format) {
        for frame in frames.take(budget) {
            let frame = frame.map_err(|_| VI_FAILED)?;
            let (numerator, denominator) = frame.delay().numer_denom_ms();
            let delay = if denominator == 0 { 0 } else { numerator / denominator };
            let mut buffer = frame.into_buffer();
            if buffer.width() != width || buffer.height() != height {
                buffer = resize_with(&buffer, width, height, 0).ok_or(VI_FAILED)?;
            }
            pixels.extend_from_slice(&to_bgra(buffer));
            delays.extend_from_slice(&delay.to_le_bytes());
        }
    }
    if pixels.is_empty() {
        let (decoded_width, decoded_height, single) = decode(input, 0, max_bytes)?;
        let image = RgbaImage::from_raw(decoded_width, decoded_height, single).ok_or(VI_FAILED)?;
        let mut rgba = image;
        for pixel in rgba.chunks_exact_mut(4) {
            pixel.swap(0, 2);
        }
        let (fit_width, fit_height) = fit_size(decoded_width, decoded_height, max_width, max_height);
        if fit_width != decoded_width || fit_height != decoded_height {
            rgba = resize_with(&rgba, fit_width, fit_height, 0).ok_or(VI_FAILED)?;
        }
        if fit_width != width || fit_height != height {
            return Ok((fit_width, fit_height, to_bgra(rgba), 0u32.to_le_bytes().to_vec()));
        }
        pixels = to_bgra(rgba);
        delays = 0u32.to_le_bytes().to_vec();
    }
    Ok((width, height, pixels, delays))
}

#[no_mangle]
pub unsafe extern "C" fn vi_decode_animation_bgra(
    data: *const u8,
    length: usize,
    max_width: u32,
    max_height: u32,
    max_frames: u32,
    max_bytes: u64,
    out_width: *mut u32,
    out_height: *mut u32,
    out_pixels: *mut *mut u8,
    out_pixel_length: *mut usize,
    out_delays: *mut *mut u8,
    out_delay_length: *mut usize,
) -> i32 {
    if data.is_null() || length == 0 || out_width.is_null() || out_height.is_null() || out_pixels.is_null() || out_pixel_length.is_null() || out_delays.is_null() || out_delay_length.is_null() {
        return VI_INVALID;
    }
    *out_pixels = std::ptr::null_mut();
    *out_pixel_length = 0;
    *out_delays = std::ptr::null_mut();
    *out_delay_length = 0;
    let input = std::slice::from_raw_parts(data, length);
    match catch_unwind(AssertUnwindSafe(|| decode_animation(input, max_width, max_height, max_frames, max_bytes))) {
        Ok(Ok((width, height, pixels, delays))) => {
            *out_width = width;
            *out_height = height;
            give(pixels, out_pixels, out_pixel_length);
            give(delays, out_delays, out_delay_length);
            VI_OK
        }
        Ok(Err(code)) => code,
        Err(_) => VI_FAILED,
    }
}

#[no_mangle]
pub unsafe extern "C" fn vi_encode_gif_frames(
    pixels: *const u8,
    length: usize,
    width: u32,
    height: u32,
    count: u32,
    delay_ms: u32,
    out_bytes: *mut *mut u8,
    out_length: *mut usize,
) -> i32 {
    if pixels.is_null() || out_bytes.is_null() || out_length.is_null() || count == 0 || width == 0 || height == 0 || width > MAX_EDGE || height > MAX_EDGE {
        return VI_INVALID;
    }
    *out_bytes = std::ptr::null_mut();
    *out_length = 0;
    let frame_bytes = (width as u64) * (height as u64) * 4;
    if frame_bytes * (count as u64) != length as u64 || length as u64 > MAX_PIXEL_BYTES {
        return VI_INVALID;
    }
    let result = catch_unwind(AssertUnwindSafe(|| -> Result<Vec<u8>, i32> {
        let mut output = Vec::new();
        {
            let mut encoder = image::codecs::gif::GifEncoder::new(&mut output);
            encoder.set_repeat(image::codecs::gif::Repeat::Infinite).map_err(|_| VI_FAILED)?;
            for index in 0..count as usize {
                let start = index * frame_bytes as usize;
                let image = bgra_image(pixels.add(start), frame_bytes as usize, width, height).ok_or(VI_INVALID)?;
                let delay = image::Delay::from_numer_denom_ms(delay_ms, 1);
                encoder.encode_frame(image::Frame::from_parts(image, 0, 0, delay)).map_err(|_| VI_FAILED)?;
            }
        }
        Ok(output)
    }));
    match result {
        Ok(Ok(data)) => {
            give(data, out_bytes, out_length);
            VI_OK
        }
        Ok(Err(code)) => code,
        Err(_) => VI_FAILED,
    }
}
