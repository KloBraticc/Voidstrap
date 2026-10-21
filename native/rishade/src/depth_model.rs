
use libloading::Library;
use ort_sys as ort;
use std::ffi::{CStr, c_void};
use std::fs;
use std::io;
use std::os::windows::ffi::OsStrExt;
use std::path::{Path, PathBuf};
use std::ptr;
use windows::Win32::Graphics::Direct3D12::{ID3D12CommandQueue, ID3D12Device, ID3D12Resource};
use windows::core::{GUID, IUnknown, Interface};

type DmlStatusFn = unsafe extern "system" fn(
    *mut ort::OrtSessionOptions,
    *mut c_void,
    *mut c_void,
) -> ort::OrtStatusPtr;
type DmlImportFn = unsafe extern "system" fn(*mut c_void, *mut *mut c_void) -> ort::OrtStatusPtr;
type DmlFreeFn = unsafe extern "system" fn(*mut c_void) -> ort::OrtStatusPtr;

#[repr(C)]
struct OrtDmlApi {
    append_provider: DmlStatusFn,
    append_provider_with_queue: DmlStatusFn,
    import_resource: DmlImportFn,
    free_resource: DmlFreeFn,
}

type GetApiBase = unsafe extern "system" fn() -> *const ort::OrtApiBase;
type DmlCreateDevice = unsafe extern "system" fn(
    *mut c_void,
    u32,
    *const GUID,
    *mut *mut c_void,
) -> windows::core::HRESULT;

const IID_IDML_DEVICE: GUID = GUID::from_u128(0x6dbd6437_96fd_423f_a98c_ae5e7c9d13d2);

pub struct DepthModel {
    _onnx_runtime: Library,
    _directml: Library,
    api: ort::OrtApi,
    dml_api: *const OrtDmlApi,
    env: *mut ort::OrtEnv,
    options: *mut ort::OrtSessionOptions,
    session: *mut ort::OrtSession,
    memory: *mut ort::OrtMemoryInfo,
    dml_device: Option<IUnknown>,
    input_name: Vec<u8>,
    output_name: Vec<u8>,
    input_shape: [i64; 4],
    output_shape: Vec<i64>,
    input_type: ort::ONNXTensorElementDataType,
    output_type: ort::ONNXTensorElementDataType,
    input_resource: Option<ID3D12Resource>,
    output_resource: Option<ID3D12Resource>,
    input_allocation: *mut c_void,
    output_allocation: *mut c_void,
    input_value: *mut ort::OrtValue,
    output_value: *mut ort::OrtValue,
    width: u32,
    height: u32,
}

impl DepthModel {

    pub fn load(
        directory: &Path,
        model_file: &str,
        width: u32,
        height: u32,
        device: &ID3D12Device,
        queue: &ID3D12CommandQueue,
    ) -> io::Result<Self> {
        if width == 0
            || height == 0
            || width > 924
            || height > 924
            || !width.is_multiple_of(14)
            || !height.is_multiple_of(14)
        {
            return Err(invalid(
                "Depth Anything V2 dimensions must be multiples of 14 no larger than 924",
            ));
        }
        let (directml_path, onnx_path, model_path) = validate_depth_files(directory, model_file)?;
        let directml = unsafe { Library::new(&directml_path) }
            .map_err(|error| io::Error::other(format!("could not load DirectML.dll: {error}")))?;
        let onnx_runtime = unsafe { Library::new(&onnx_path) }.map_err(|error| {
            io::Error::other(format!("could not load onnxruntime.dll: {error}"))
        })?;
        let get_api_base: GetApiBase = unsafe {
            *onnx_runtime
                .get::<GetApiBase>(b"OrtGetApiBase\0")
                .map_err(|error| {
                    io::Error::other(format!("ONNX Runtime API entry point is missing: {error}"))
                })?
        };
        let base = unsafe { get_api_base() };
        if base.is_null() {
            return Err(invalid("ONNX Runtime returned a null API base"));
        }
        let api_pointer = unsafe { ((*base).GetApi)(ort::ORT_API_VERSION) };
        if api_pointer.is_null() {
            return Err(invalid(
                "ONNX Runtime does not support the required C API version",
            ));
        }
        let api = unsafe { *api_pointer };
        let mut provider_api = ptr::null();
        check(&api, unsafe {
            (api.GetExecutionProviderApi)(c"DML".as_ptr(), ort::ORT_API_VERSION, &mut provider_api)
        })?;
        if provider_api.is_null() {
            return Err(invalid("ONNX Runtime has no DirectML provider API"));
        }
        let dml_api = provider_api.cast::<OrtDmlApi>();
        let mut model = Self {
            _onnx_runtime: onnx_runtime,
            _directml: directml,
            api,
            dml_api,
            env: ptr::null_mut(),
            options: ptr::null_mut(),
            session: ptr::null_mut(),
            memory: ptr::null_mut(),
            dml_device: None,
            input_name: Vec::new(),
            output_name: Vec::new(),
            input_shape: [1, 3, i64::from(height), i64::from(width)],
            output_shape: Vec::new(),
            input_type: ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_UNDEFINED,
            output_type: ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_UNDEFINED,
            input_resource: None,
            output_resource: None,
            input_allocation: ptr::null_mut(),
            output_allocation: ptr::null_mut(),
            input_value: ptr::null_mut(),
            output_value: ptr::null_mut(),
            width,
            height,
        };
        model.initialize(&model_path, device, queue)?;
        Ok(model)
    }

    fn initialize(
        &mut self,
        model_path: &Path,
        device: &ID3D12Device,
        queue: &ID3D12CommandQueue,
    ) -> io::Result<()> {
        check(&self.api, unsafe {
            (self.api.CreateEnv)(
                ort::OrtLoggingLevel::ORT_LOGGING_LEVEL_ERROR,
                c"RiShade".as_ptr(),
                &mut self.env,
            )
        })?;
        check(&self.api, unsafe {
            (self.api.CreateSessionOptions)(&mut self.options)
        })?;
        check(&self.api, unsafe {
            (self.api.SetSessionExecutionMode)(self.options, ort::ExecutionMode::ORT_SEQUENTIAL)
        })?;
        check(&self.api, unsafe {
            (self.api.DisableMemPattern)(self.options)
        })?;
        check(&self.api, unsafe {
            (self.api.SetSessionGraphOptimizationLevel)(
                self.options,
                ort::GraphOptimizationLevel::ORT_ENABLE_ALL,
            )
        })?;
        check(&self.api, unsafe {
            (self.api.AddSessionConfigEntry)(
                self.options,
                c"session.intra_op.allow_spinning".as_ptr(),
                c"0".as_ptr(),
            )
        })?;
        for (name, value) in [
            (c"batch_size", 1i64),
            (c"height", i64::from(self.height)),
            (c"width", i64::from(self.width)),
        ] {
            check(&self.api, unsafe {
                (self.api.AddFreeDimensionOverrideByName)(self.options, name.as_ptr(), value)
            })?;
        }
        let create_dml_device: DmlCreateDevice = unsafe {
            *self
                ._directml
                .get::<DmlCreateDevice>(b"DMLCreateDevice\0")
                .map_err(|error| {
                    io::Error::other(format!("DirectML device entry point is missing: {error}"))
                })?
        };
        let mut dml_device = ptr::null_mut();
        unsafe { create_dml_device(device.as_raw(), 0, &IID_IDML_DEVICE, &mut dml_device) }
            .ok()
            .map_err(|error| {
                io::Error::other(format!("could not create DirectML device: {error}"))
            })?;
        if dml_device.is_null() {
            return Err(invalid("DirectML returned a null device"));
        }
        self.dml_device = Some(unsafe { IUnknown::from_raw(dml_device) });
        let append_provider = unsafe { (*self.dml_api).append_provider_with_queue };
        check(&self.api, unsafe {
            append_provider(
                self.options,
                self.dml_device.as_ref().unwrap().as_raw(),
                queue.as_raw(),
            )
        })?;

        let model_path_wide = model_path
            .as_os_str()
            .encode_wide()
            .chain(Some(0))
            .collect::<Vec<_>>();
        check(&self.api, unsafe {
            (self.api.CreateSession)(
                self.env,
                model_path_wide.as_ptr(),
                self.options,
                &mut self.session,
            )
        })?;
        check(&self.api, unsafe {
            (self.api.CreateMemoryInfo)(
                c"DML".as_ptr(),
                ort::OrtAllocatorType::OrtDeviceAllocator,
                0,
                ort::OrtMemType::OrtMemTypeDefault,
                &mut self.memory,
            )
        })?;
        self.read_model_signature()?;
        Ok(())
    }

    fn read_model_signature(&mut self) -> io::Result<()> {
        let mut input_count = 0;
        let mut output_count = 0;
        check(&self.api, unsafe {
            (self.api.SessionGetInputCount)(self.session, &mut input_count)
        })?;
        check(&self.api, unsafe {
            (self.api.SessionGetOutputCount)(self.session, &mut output_count)
        })?;
        if input_count != 1 || output_count != 1 {
            return Err(invalid(
                "depth model must have exactly one input and one output",
            ));
        }
        let mut allocator = ptr::null_mut();
        check(&self.api, unsafe {
            (self.api.GetAllocatorWithDefaultOptions)(&mut allocator)
        })?;
        self.input_name = self.read_name(allocator, true)?;
        self.output_name = self.read_name(allocator, false)?;
        self.input_type = self.read_tensor_type(true)?;
        self.output_type = self.read_tensor_type(false)?;
        if !supported_float(self.input_type) || !supported_float(self.output_type) {
            return Err(invalid(
                "depth model input and output must use float16 or float32",
            ));
        }
        let output_rank = self.read_output_rank()?;
        self.output_shape = match output_rank {
            3 => vec![1, i64::from(self.height), i64::from(self.width)],
            4 => vec![1, 1, i64::from(self.height), i64::from(self.width)],
            _ => {
                return Err(invalid(
                    "depth model output must be a three- or four-dimensional depth map",
                ));
            }
        };
        Ok(())
    }

    fn read_name(&self, allocator: *mut ort::OrtAllocator, input: bool) -> io::Result<Vec<u8>> {
        let mut name = ptr::null_mut();
        let status = unsafe {
            if input {
                (self.api.SessionGetInputName)(self.session, 0, allocator, &mut name)
            } else {
                (self.api.SessionGetOutputName)(self.session, 0, allocator, &mut name)
            }
        };
        check(&self.api, status)?;
        if name.is_null() {
            return Err(invalid("depth model returned an empty tensor name"));
        }
        let bytes = unsafe { CStr::from_ptr(name) }.to_bytes_with_nul().to_vec();
        check(&self.api, unsafe {
            (self.api.AllocatorFree)(allocator, name.cast())
        })?;
        Ok(bytes)
    }

    fn read_tensor_type(&self, input: bool) -> io::Result<ort::ONNXTensorElementDataType> {
        let mut type_info = ptr::null_mut();
        let status = unsafe {
            if input {
                (self.api.SessionGetInputTypeInfo)(self.session, 0, &mut type_info)
            } else {
                (self.api.SessionGetOutputTypeInfo)(self.session, 0, &mut type_info)
            }
        };
        check(&self.api, status)?;
        let mut tensor_info = ptr::null();
        let result = check(&self.api, unsafe {
            (self.api.CastTypeInfoToTensorInfo)(type_info, &mut tensor_info)
        })
        .and_then(|()| {
            let mut tensor_type =
                ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_UNDEFINED;
            check(&self.api, unsafe {
                (self.api.GetTensorElementType)(tensor_info, &mut tensor_type)
            })?;
            Ok(tensor_type)
        });
        unsafe { (self.api.ReleaseTypeInfo)(type_info) };
        result
    }

    fn read_output_rank(&self) -> io::Result<usize> {
        let mut type_info = ptr::null_mut();
        check(&self.api, unsafe {
            (self.api.SessionGetOutputTypeInfo)(self.session, 0, &mut type_info)
        })?;
        let mut tensor_info = ptr::null();
        let result = check(&self.api, unsafe {
            (self.api.CastTypeInfoToTensorInfo)(type_info, &mut tensor_info)
        })
        .and_then(|()| {
            let mut rank = 0;
            check(&self.api, unsafe {
                (self.api.GetDimensionsCount)(tensor_info, &mut rank)
            })?;
            Ok(rank)
        });
        unsafe { (self.api.ReleaseTypeInfo)(type_info) };
        result
    }

    pub fn bind(&mut self, input: &ID3D12Resource, output: &ID3D12Resource) -> io::Result<()> {
        self.unbind();
        self.input_resource = Some(input.clone());
        self.output_resource = Some(output.clone());
        let import_resource = unsafe { (*self.dml_api).import_resource };
        check(&self.api, unsafe {
            import_resource(
                self.input_resource.as_ref().unwrap().as_raw(),
                &mut self.input_allocation,
            )
        })?;
        check(&self.api, unsafe {
            import_resource(
                self.output_resource.as_ref().unwrap().as_raw(),
                &mut self.output_allocation,
            )
        })?;
        let input_len = tensor_byte_len(&self.input_shape, self.input_type)?;
        let output_len = tensor_byte_len(&self.output_shape, self.output_type)?;
        check(&self.api, unsafe {
            (self.api.CreateTensorWithDataAsOrtValue)(
                self.memory,
                self.input_allocation,
                input_len,
                self.input_shape.as_ptr(),
                self.input_shape.len(),
                self.input_type,
                &mut self.input_value,
            )
        })?;
        check(&self.api, unsafe {
            (self.api.CreateTensorWithDataAsOrtValue)(
                self.memory,
                self.output_allocation,
                output_len,
                self.output_shape.as_ptr(),
                self.output_shape.len(),
                self.output_type,
                &mut self.output_value,
            )
        })?;
        Ok(())
    }

    pub fn run(&mut self) -> io::Result<()> {
        if self.input_value.is_null() || self.output_value.is_null() {
            return Err(invalid("depth model has no imported GPU buffers"));
        }
        let input = self.input_value as *const ort::OrtValue;
        let mut output = self.output_value;
        let input_names = [self.input_name.as_ptr().cast()];
        let output_names = [self.output_name.as_ptr().cast()];
        let inputs = [input];
        let result = check(&self.api, unsafe {
            (self.api.Run)(
                self.session,
                ptr::null(),
                input_names.as_ptr(),
                inputs.as_ptr(),
                1,
                output_names.as_ptr(),
                1,
                &mut output,
            )
        });
        if result.is_ok() {
            self.output_value = output;
        }
        result
    }

    pub fn input_is_half(&self) -> bool {
        self.input_type == ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT16
    }

    pub fn output_is_half(&self) -> bool {
        self.output_type == ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT16
    }

    fn unbind(&mut self) {
        unsafe {
            if !self.input_value.is_null() {
                (self.api.ReleaseValue)(self.input_value);
                self.input_value = ptr::null_mut();
            }
            if !self.output_value.is_null() {
                (self.api.ReleaseValue)(self.output_value);
                self.output_value = ptr::null_mut();
            }
            if !self.input_allocation.is_null() {
                let _ = check(
                    &self.api,
                    ((*self.dml_api).free_resource)(self.input_allocation),
                );
                self.input_allocation = ptr::null_mut();
            }
            if !self.output_allocation.is_null() {
                let _ = check(
                    &self.api,
                    ((*self.dml_api).free_resource)(self.output_allocation),
                );
                self.output_allocation = ptr::null_mut();
            }
            self.input_resource = None;
            self.output_resource = None;
        }
    }
}

impl Drop for DepthModel {
    fn drop(&mut self) {
        unsafe {
            if !self.session.is_null() {
                (self.api.ReleaseSession)(self.session);
            }
            self.unbind();
            if !self.options.is_null() {
                (self.api.ReleaseSessionOptions)(self.options);
            }
            if !self.memory.is_null() {
                (self.api.ReleaseMemoryInfo)(self.memory);
            }
            if !self.env.is_null() {
                (self.api.ReleaseEnv)(self.env);
            }
        }
    }
}

fn check(api: &ort::OrtApi, status: ort::OrtStatusPtr) -> io::Result<()> {
    if status.0.is_null() {
        return Ok(());
    }
    let message = unsafe { CStr::from_ptr((api.GetErrorMessage)(status.0)) }
        .to_string_lossy()
        .into_owned();
    unsafe { (api.ReleaseStatus)(status.0) };
    Err(io::Error::other(message))
}

fn supported_float(value: ort::ONNXTensorElementDataType) -> bool {
    matches!(
        value,
        ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT
            | ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT16
    )
}

fn tensor_byte_len(shape: &[i64], type_: ort::ONNXTensorElementDataType) -> io::Result<usize> {
    let elements = shape
        .iter()
        .try_fold(1usize, |count, dimension| {
            usize::try_from(*dimension)
                .ok()
                .and_then(|size| count.checked_mul(size))
        })
        .ok_or_else(|| invalid("depth model tensor size overflowed"))?;
    elements
        .checked_mul(
            if type_ == ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT16 {
                2
            } else {
                4
            },
        )
        .ok_or_else(|| invalid("depth model tensor byte size overflowed"))
}

fn invalid(message: &str) -> io::Error {
    io::Error::new(io::ErrorKind::InvalidData, message)
}

fn validate_depth_files(
    directory: &Path,
    model_file: &str,
) -> io::Result<(PathBuf, PathBuf, PathBuf)> {
    if model_file != "depth-anything-v2-small.onnx" {
        return Err(invalid("unsupported depth model file name"));
    }
    if !directory.is_absolute() {
        return Err(invalid("depth asset directory must be absolute"));
    }
    let root = fs::canonicalize(directory)?;
    if !fs::metadata(&root)?.is_dir() {
        return Err(invalid("depth asset path is not a directory"));
    }
    let directml = regular_child(&root, "DirectML.dll")?;
    let onnx_runtime = regular_child(&root, "onnxruntime.dll")?;
    let model = regular_child(&root, model_file)?;
    Ok((directml, onnx_runtime, model))
}

fn regular_child(root: &Path, name: &str) -> io::Result<PathBuf> {
    let path = root.join(name);
    let metadata = fs::symlink_metadata(&path)?;
    if metadata.file_type().is_symlink() || !metadata.is_file() {
        return Err(invalid(
            "depth asset must be a regular file inside its extension directory",
        ));
    }
    Ok(path)
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::os::windows::fs::symlink_file;

    #[test]
    fn depth_assets_must_be_regular_files_in_the_extension_directory() {
        let directory = tempfile::tempdir().unwrap();
        for name in [
            "DirectML.dll",
            "onnxruntime.dll",
            "depth-anything-v2-small.onnx",
        ] {
            fs::write(directory.path().join(name), []).unwrap();
        }
        assert!(validate_depth_files(directory.path(), "depth-anything-v2-small.onnx").is_ok());
        assert!(validate_depth_files(directory.path(), "..\\outside.onnx").is_err());

        let outside = directory.path().join("outside.onnx");
        fs::write(&outside, []).unwrap();
        let model = directory.path().join("depth-anything-v2-small.onnx");
        fs::remove_file(&model).unwrap();
        if symlink_file(outside, &model).is_ok() {
            assert!(
                validate_depth_files(directory.path(), "depth-anything-v2-small.onnx").is_err()
            );
        }
    }

    #[test]
    fn tensor_sizes_validate_overflow_and_type_width() {
        assert_eq!(
            tensor_byte_len(
                &[1, 3, 14, 14],
                ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT
            )
            .unwrap(),
            3 * 14 * 14 * 4
        );
        assert_eq!(
            tensor_byte_len(
                &[1, 1, 14, 14],
                ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT16
            )
            .unwrap(),
            14 * 14 * 2
        );
        assert!(
            tensor_byte_len(
                &[-1, 3, 14, 14],
                ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT
            )
            .is_err()
        );
        assert!(
            tensor_byte_len(
                &[i64::MAX, i64::MAX],
                ort::ONNXTensorElementDataType::ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT
            )
            .is_err()
        );
    }
}
