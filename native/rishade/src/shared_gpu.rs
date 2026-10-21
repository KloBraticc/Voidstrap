use std::io;
use windows::Win32::Foundation::{CloseHandle, HANDLE};
use windows::Win32::Graphics::Direct3D11::{
    D3D11_RESOURCE_MISC_SHARED, D3D11_RESOURCE_MISC_SHARED_NTHANDLE, D3D11_TEXTURE2D_DESC,
    D3D11_USAGE_DEFAULT, ID3D11Device, ID3D11Device5, ID3D11DeviceContext, ID3D11DeviceContext4,
    ID3D11Fence, ID3D11Texture2D,
};
use windows::Win32::Graphics::Direct3D12::{
    D3D12_COMMAND_LIST_TYPE_DIRECT, D3D12_FENCE_FLAG_SHARED, D3D12_HEAP_FLAG_NONE,
    D3D12_HEAP_PROPERTIES, D3D12_HEAP_TYPE_DEFAULT, D3D12_PLACED_SUBRESOURCE_FOOTPRINT,
    D3D12_RESOURCE_BARRIER, D3D12_RESOURCE_BARRIER_0, D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
    D3D12_RESOURCE_BARRIER_FLAG_NONE, D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
    D3D12_RESOURCE_DIMENSION_BUFFER, D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
    D3D12_RESOURCE_STATE_COMMON, D3D12_RESOURCE_STATE_COPY_DEST, D3D12_RESOURCE_TRANSITION_BARRIER,
    D3D12_SUBRESOURCE_FOOTPRINT, D3D12_TEXTURE_COPY_LOCATION, D3D12_TEXTURE_COPY_LOCATION_0,
    D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT, D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
    D3D12_TEXTURE_LAYOUT_ROW_MAJOR, ID3D12CommandAllocator, ID3D12CommandList, ID3D12CommandQueue,
    ID3D12Device, ID3D12Fence, ID3D12GraphicsCommandList, ID3D12PipelineState, ID3D12Resource,
};
use windows::Win32::Graphics::Dxgi::Common::{
    DXGI_FORMAT_R16_FLOAT, DXGI_FORMAT_R32_FLOAT, DXGI_FORMAT_UNKNOWN, DXGI_SAMPLE_DESC,
};
use windows::Win32::Graphics::Dxgi::{
    DXGI_SHARED_RESOURCE_READ, DXGI_SHARED_RESOURCE_WRITE, IDXGIResource1,
};
use windows::core::{Interface, PCWSTR};

const SHARED_ROW_BYTES: u32 = 16 * 256;

pub struct SharedTensorTexture {
    pub d3d11_texture: ID3D11Texture2D,
    pub d3d12_texture: ID3D12Resource,
    pub row_pitch: u32,
    pub height: u32,
    handle: HANDLE,
}

impl SharedTensorTexture {
    pub fn new(
        d3d11_device: &ID3D11Device,
        d3d12_device: &ID3D12Device,
        elements: usize,
        half_precision: bool,
        bind_flags: u32,
    ) -> io::Result<Self> {
        let (width, height) =
            shared_tensor_dimensions(elements, half_precision).ok_or_else(|| {
                io::Error::new(io::ErrorKind::InvalidInput, "invalid shared tensor size")
            })?;
        let format = if half_precision {
            DXGI_FORMAT_R16_FLOAT
        } else {
            DXGI_FORMAT_R32_FLOAT
        };
        let desc = D3D11_TEXTURE2D_DESC {
            Width: width,
            Height: height,
            MipLevels: 1,
            ArraySize: 1,
            Format: format,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Usage: D3D11_USAGE_DEFAULT,
            BindFlags: bind_flags,
            CPUAccessFlags: 0,
            MiscFlags: (D3D11_RESOURCE_MISC_SHARED | D3D11_RESOURCE_MISC_SHARED_NTHANDLE).0 as u32,
        };
        let mut d3d11_texture: Option<ID3D11Texture2D> = None;
        unsafe { d3d11_device.CreateTexture2D(&desc, None, Some(&mut d3d11_texture)) }.map_err(
            |error| io::Error::other(format!("could not create shared tensor texture: {error}")),
        )?;
        let d3d11_texture = d3d11_texture
            .ok_or_else(|| io::Error::other("D3D11 returned a null shared tensor texture"))?;
        let dxgi_resource = d3d11_texture.cast::<IDXGIResource1>().map_err(|error| {
            io::Error::other(format!("shared tensor is not a DXGI resource: {error}"))
        })?;
        let handle = unsafe {
            dxgi_resource.CreateSharedHandle(
                None,
                DXGI_SHARED_RESOURCE_READ.0 | DXGI_SHARED_RESOURCE_WRITE.0,
                PCWSTR::null(),
            )
        }
        .map_err(|error| io::Error::other(format!("could not share tensor texture: {error}")))?;
        let mut d3d12_texture = None;
        if let Err(error) = unsafe { d3d12_device.OpenSharedHandle(handle, &mut d3d12_texture) } {
            let _ = unsafe { CloseHandle(handle) };
            return Err(io::Error::other(format!(
                "could not open tensor texture on D3D12: {error}"
            )));
        }
        let Some(d3d12_texture) = d3d12_texture else {
            let _ = unsafe { CloseHandle(handle) };
            return Err(io::Error::other(
                "D3D12 returned a null shared tensor texture",
            ));
        };
        Ok(Self {
            d3d11_texture,
            d3d12_texture,
            row_pitch: SHARED_ROW_BYTES,
            height,
            handle,
        })
    }

    pub fn create_packed_buffer(&self, device: &ID3D12Device) -> io::Result<ID3D12Resource> {
        let width = tensor_buffer_byte_len(self.height).ok_or_else(|| {
            io::Error::new(io::ErrorKind::InvalidInput, "tensor buffer size overflowed")
        })?;
        let heap = D3D12_HEAP_PROPERTIES {
            Type: D3D12_HEAP_TYPE_DEFAULT,
            CreationNodeMask: 1,
            VisibleNodeMask: 1,
            ..Default::default()
        };
        let desc = windows::Win32::Graphics::Direct3D12::D3D12_RESOURCE_DESC {
            Dimension: D3D12_RESOURCE_DIMENSION_BUFFER,
            Width: width,
            Height: 1,
            DepthOrArraySize: 1,
            MipLevels: 1,
            Format: DXGI_FORMAT_UNKNOWN,
            SampleDesc: DXGI_SAMPLE_DESC {
                Count: 1,
                Quality: 0,
            },
            Layout: D3D12_TEXTURE_LAYOUT_ROW_MAJOR,
            Flags: D3D12_RESOURCE_FLAG_ALLOW_UNORDERED_ACCESS,
            ..Default::default()
        };
        let mut buffer = None;
        unsafe {
            device.CreateCommittedResource(
                &heap,
                D3D12_HEAP_FLAG_NONE,
                &desc,
                D3D12_RESOURCE_STATE_COMMON,
                None,
                &mut buffer,
            )
        }
        .map_err(|error| {
            io::Error::other(format!("could not create model tensor buffer: {error}"))
        })?;
        buffer.ok_or_else(|| io::Error::other("D3D12 returned a null model tensor buffer"))
    }
}

pub struct D3D12TensorCopy {
    allocator: ID3D12CommandAllocator,
    list: ID3D12GraphicsCommandList,
}

impl D3D12TensorCopy {
    pub fn new(device: &ID3D12Device) -> io::Result<Self> {
        let allocator = unsafe { device.CreateCommandAllocator(D3D12_COMMAND_LIST_TYPE_DIRECT) }
            .map_err(|error| {
                io::Error::other(format!("could not create copy allocator: {error}"))
            })?;
        let list = unsafe {
            device.CreateCommandList::<_, _, ID3D12GraphicsCommandList>(
                0,
                D3D12_COMMAND_LIST_TYPE_DIRECT,
                &allocator,
                None::<&ID3D12PipelineState>,
            )
        }
        .map_err(|error| {
            io::Error::other(format!("could not create copy command list: {error}"))
        })?;
        unsafe { list.Close() }.map_err(|error| {
            io::Error::other(format!("could not close initial copy list: {error}"))
        })?;
        Ok(Self { allocator, list })
    }

    pub fn copy_to_buffer(
        &self,
        queue: &ID3D12CommandQueue,
        texture: &SharedTensorTexture,
        buffer: &ID3D12Resource,
        half_precision: bool,
    ) -> io::Result<()> {
        let source = texture_location(texture);
        let destination = buffer_location(buffer, texture, half_precision);
        self.record_and_submit(queue, || unsafe {
            self.list
                .CopyTextureRegion(&destination, 0, 0, 0, &source, None)
        })
    }

    pub fn copy_from_buffer(
        &self,
        queue: &ID3D12CommandQueue,
        texture: &SharedTensorTexture,
        buffer: &ID3D12Resource,
        half_precision: bool,
    ) -> io::Result<()> {
        let destination = texture_location(texture);
        let source = buffer_location(buffer, texture, half_precision);
        self.record_and_submit(queue, || unsafe {
            self.list
                .CopyTextureRegion(&destination, 0, 0, 0, &source, None);
            let transition = D3D12_RESOURCE_TRANSITION_BARRIER {
                pResource: std::mem::ManuallyDrop::new(Some(texture.d3d12_texture.clone())),
                Subresource: D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES,
                StateBefore: D3D12_RESOURCE_STATE_COPY_DEST,
                StateAfter: D3D12_RESOURCE_STATE_COMMON,
            };
            self.list.ResourceBarrier(&[D3D12_RESOURCE_BARRIER {
                Type: D3D12_RESOURCE_BARRIER_TYPE_TRANSITION,
                Flags: D3D12_RESOURCE_BARRIER_FLAG_NONE,
                Anonymous: D3D12_RESOURCE_BARRIER_0 {
                    Transition: std::mem::ManuallyDrop::new(transition),
                },
            }]);
        })
    }

    fn record_and_submit(
        &self,
        queue: &ID3D12CommandQueue,
        record: impl FnOnce(),
    ) -> io::Result<()> {
        unsafe { self.allocator.Reset() }.map_err(|error| {
            io::Error::other(format!("could not reset copy allocator: {error}"))
        })?;
        unsafe {
            self.list
                .Reset(&self.allocator, None::<&ID3D12PipelineState>)
        }
        .map_err(|error| io::Error::other(format!("could not reset copy list: {error}")))?;
        record();
        unsafe { self.list.Close() }
            .map_err(|error| io::Error::other(format!("could not close copy list: {error}")))?;
        let command_list = self
            .list
            .cast::<ID3D12CommandList>()
            .map_err(|error| io::Error::other(format!("could not submit copy list: {error}")))?;
        unsafe { queue.ExecuteCommandLists(&[Some(command_list)]) };
        Ok(())
    }
}

fn texture_location(texture: &SharedTensorTexture) -> D3D12_TEXTURE_COPY_LOCATION {
    D3D12_TEXTURE_COPY_LOCATION {
        pResource: std::mem::ManuallyDrop::new(Some(texture.d3d12_texture.clone())),
        Type: D3D12_TEXTURE_COPY_TYPE_SUBRESOURCE_INDEX,
        Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
            SubresourceIndex: 0,
        },
    }
}

fn buffer_location(
    buffer: &ID3D12Resource,
    texture: &SharedTensorTexture,
    half_precision: bool,
) -> D3D12_TEXTURE_COPY_LOCATION {
    let width = if half_precision {
        SHARED_ROW_BYTES / 2
    } else {
        SHARED_ROW_BYTES / 4
    };
    let format = if half_precision {
        DXGI_FORMAT_R16_FLOAT
    } else {
        DXGI_FORMAT_R32_FLOAT
    };
    D3D12_TEXTURE_COPY_LOCATION {
        pResource: std::mem::ManuallyDrop::new(Some(buffer.clone())),
        Type: D3D12_TEXTURE_COPY_TYPE_PLACED_FOOTPRINT,
        Anonymous: D3D12_TEXTURE_COPY_LOCATION_0 {
            PlacedFootprint: D3D12_PLACED_SUBRESOURCE_FOOTPRINT {
                Offset: 0,
                Footprint: D3D12_SUBRESOURCE_FOOTPRINT {
                    Format: format,
                    Width: width,
                    Height: texture.height,
                    Depth: 1,
                    RowPitch: texture.row_pitch,
                },
            },
        },
    }
}

impl Drop for SharedTensorTexture {
    fn drop(&mut self) {
        if !self.handle.is_invalid() {
            let _ = unsafe { CloseHandle(self.handle) };
            self.handle = HANDLE::default();
        }
    }
}

fn shared_tensor_dimensions(elements: usize, half_precision: bool) -> Option<(u32, u32)> {
    if elements == 0 {
        return None;
    }
    let bytes_per_element = if half_precision { 2 } else { 4 };
    let width = SHARED_ROW_BYTES / bytes_per_element;
    let height = elements.div_ceil(width as usize);
    let height = u32::try_from(height).ok()?;
    (height <= 16_384).then_some((width, height))
}

fn tensor_buffer_byte_len(height: u32) -> Option<u64> {
    u64::from(height).checked_mul(u64::from(SHARED_ROW_BYTES))
}

#[derive(Clone, Copy, Debug, Eq, PartialEq)]
pub struct FenceValues {
    pub input_ready: u64,
    pub output_ready: u64,
}

#[derive(Default)]
struct FenceSequence(u64);

impl FenceSequence {
    fn reserve(&mut self) -> io::Result<FenceValues> {
        let input_ready = self
            .0
            .checked_add(1)
            .ok_or_else(|| io::Error::other("depth fence value overflowed"))?;
        let output_ready = input_ready
            .checked_add(1)
            .ok_or_else(|| io::Error::other("depth fence value overflowed"))?;
        self.0 = output_ready;
        Ok(FenceValues {
            input_ready,
            output_ready,
        })
    }
}

pub struct SharedDepthFence {
    d3d12: ID3D12Fence,
    d3d11: ID3D11Fence,
    handle: HANDLE,
    sequence: FenceSequence,
}

impl SharedDepthFence {
    pub fn new(d3d12_device: &ID3D12Device, d3d11_device: &ID3D11Device) -> io::Result<Self> {
        let d3d12 =
            unsafe { d3d12_device.CreateFence(0, D3D12_FENCE_FLAG_SHARED) }.map_err(|error| {
                io::Error::other(format!("could not create shared GPU fence: {error}"))
            })?;
        let handle = unsafe { d3d12_device.CreateSharedHandle(&d3d12, None, 0x1000_0000, None) }
            .map_err(|error| io::Error::other(format!("could not share GPU fence: {error}")))?;
        let d3d11 = match d3d11_device.cast::<ID3D11Device5>().and_then(|device| {
            let mut fence = None;
            unsafe { device.OpenSharedFence(handle, &mut fence)? };
            fence.ok_or_else(windows::core::Error::from_win32)
        }) {
            Ok(fence) => fence,
            Err(error) => {
                let _ = unsafe { CloseHandle(handle) };
                return Err(io::Error::other(format!(
                    "could not open shared GPU fence: {error}"
                )));
            }
        };
        Ok(Self {
            d3d12,
            d3d11,
            handle,
            sequence: FenceSequence::default(),
        })
    }

    pub fn signal_input(&mut self, context: &ID3D11DeviceContext) -> io::Result<FenceValues> {
        let values = self.sequence.reserve()?;
        let context = context.cast::<ID3D11DeviceContext4>().map_err(|error| {
            io::Error::other(format!("D3D11 fence signaling is unavailable: {error}"))
        })?;
        unsafe { context.Signal(&self.d3d11, values.input_ready) }.map_err(|error| {
            io::Error::other(format!("could not signal depth input fence: {error}"))
        })?;
        Ok(values)
    }

    pub fn queue_wait_for_input(
        &self,
        queue: &ID3D12CommandQueue,
        values: FenceValues,
    ) -> io::Result<()> {
        unsafe { queue.Wait(&self.d3d12, values.input_ready) }
            .map_err(|error| io::Error::other(format!("could not wait for depth input: {error}")))
    }

    pub fn queue_signal_output(
        &self,
        queue: &ID3D12CommandQueue,
        values: FenceValues,
    ) -> io::Result<()> {
        unsafe { queue.Signal(&self.d3d12, values.output_ready) }
            .map_err(|error| io::Error::other(format!("could not signal depth output: {error}")))
    }

    pub fn wait_for_output(
        &self,
        context: &ID3D11DeviceContext,
        values: FenceValues,
    ) -> io::Result<()> {
        let context = context.cast::<ID3D11DeviceContext4>().map_err(|error| {
            io::Error::other(format!("D3D11 fence waiting is unavailable: {error}"))
        })?;
        unsafe { context.Wait(&self.d3d11, values.output_ready) }
            .map_err(|error| io::Error::other(format!("could not wait for depth output: {error}")))
    }

    pub fn completed_value(&self) -> u64 {
        unsafe { self.d3d12.GetCompletedValue() }
    }
}

impl Drop for SharedDepthFence {
    fn drop(&mut self) {
        if !self.handle.is_invalid() {
            let _ = unsafe { CloseHandle(self.handle) };
            self.handle = HANDLE::default();
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn shared_tensor_rows_match_the_model_copy_pitch() {
        assert_eq!(shared_tensor_dimensions(1024 * 2, false), Some((1024, 2)));
        assert_eq!(shared_tensor_dimensions(2048 * 3, true), Some((2048, 3)));
        assert_eq!(shared_tensor_dimensions(0, false), None);
        assert_eq!(shared_tensor_dimensions(1024 * 16_385, false), None);
        assert_eq!(tensor_buffer_byte_len(3), Some(12_288));
        assert_eq!(
            tensor_buffer_byte_len(u32::MAX),
            Some(u64::from(u32::MAX) * 4096)
        );
    }

    #[test]
    fn fence_values_alternate_input_and_output_without_reuse() {
        let mut sequence = FenceSequence::default();
        assert_eq!(
            sequence.reserve().unwrap(),
            FenceValues {
                input_ready: 1,
                output_ready: 2
            }
        );
        assert_eq!(
            sequence.reserve().unwrap(),
            FenceValues {
                input_ready: 3,
                output_ready: 4
            }
        );
    }

    #[test]
    fn fence_sequence_rejects_wraparound() {
        let mut sequence = FenceSequence(u64::MAX - 1);
        assert!(sequence.reserve().is_err());
    }
}
