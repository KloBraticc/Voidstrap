#include "depth_model.h"

#include <windows.h>
#include <d3d12.h>
#include <onnxruntime_c_api.h>
#include <dml_provider_factory.h>

#include <initializer_list>
#include <stdexcept>
#include <utility>

namespace
{
struct OrtError
{
    const OrtApi* api;
    void operator()(OrtStatus* status) const
    {
        if (!status)
            return;
        std::string message = api->GetErrorMessage(status);
        api->ReleaseStatus(status);
        throw std::runtime_error(message);
    }
};

struct TensorInfo
{
    ONNXTensorElementDataType type = ONNX_TENSOR_ELEMENT_DATA_TYPE_UNDEFINED;
    size_t rank = 0;
};

TensorInfo ReadTensorInfo(const OrtApi* api, OrtTypeInfo* info)
{
    TensorInfo tensor;
    const OrtTensorTypeAndShapeInfo* shape = nullptr;
    OrtStatus* status = api->CastTypeInfoToTensorInfo(info, &shape);
    if (!status)
        status = api->GetTensorElementType(shape, &tensor.type);
    if (!status)
        status = api->GetDimensionsCount(shape, &tensor.rank);
    api->ReleaseTypeInfo(info);
    OrtError{ api }(status);
    return tensor;
}

bool IsHalf(ONNXTensorElementDataType type, const char* what)
{
    if (type == ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT16)
        return true;
    if (type == ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT)
        return false;
    throw std::runtime_error(std::string("The model ") + what + " is neither float32 nor float16.");
}
}

DepthModel::~DepthModel()
{
    if (api)
    {
        if (session)
            api->ReleaseSession(session);
        Unbind();
        if (options)
            api->ReleaseSessionOptions(options);
        if (memory)
            api->ReleaseMemoryInfo(memory);
        if (env)
            api->ReleaseEnv(env);
    }
    if (queue)
        queue->Release();
    if (dml)
        dml->Release();
    if (library)
        FreeLibrary(static_cast<HMODULE>(library));
    if (dmlLibrary)
        FreeLibrary(static_cast<HMODULE>(dmlLibrary));
}

void DepthModel::Load(const std::wstring& directory, const std::wstring& modelFile, int width, int height, ID3D12Device* device)
{
    library = LoadLibraryW((directory + L"onnxruntime.dll").c_str());
    dmlLibrary = LoadLibraryW((directory + L"DirectML.dll").c_str());
    if (!library || !dmlLibrary)
        throw std::runtime_error("onnxruntime.dll or DirectML.dll could not be loaded.");

    using GetApiBase = const OrtApiBase*(ORT_API_CALL*)();
    auto getApiBase = reinterpret_cast<GetApiBase>(GetProcAddress(static_cast<HMODULE>(library), "OrtGetApiBase"));
    using CreateDml = HRESULT(WINAPI*)(ID3D12Device*, DML_CREATE_DEVICE_FLAGS, REFIID, void**);
    auto createDml = reinterpret_cast<CreateDml>(GetProcAddress(static_cast<HMODULE>(dmlLibrary), "DMLCreateDevice"));
    if (!getApiBase || !createDml)
        throw std::runtime_error("onnxruntime.dll or DirectML.dll is not the expected build.");
    api = getApiBase()->GetApi(ORT_API_VERSION);
    if (!api)
        throw std::runtime_error("onnxruntime.dll is older than the version this host was built for.");
    const OrtError check{ api };
    check(api->GetExecutionProviderApi("DML", ORT_API_VERSION, reinterpret_cast<const void**>(&dmlApi)));

    if (FAILED(createDml(device, DML_CREATE_DEVICE_FLAG_NONE, IID_PPV_ARGS(&dml))))
        throw std::runtime_error("DirectML could not use this GPU.");
    const D3D12_COMMAND_QUEUE_DESC queueDesc{ D3D12_COMMAND_LIST_TYPE_DIRECT };
    if (FAILED(device->CreateCommandQueue(&queueDesc, IID_PPV_ARGS(&queue))))
        throw std::runtime_error("The GPU command queue could not be created.");

    check(api->CreateEnv(ORT_LOGGING_LEVEL_ERROR, "Unishade", &env));
    check(api->CreateSessionOptions(&options));
    check(api->SetSessionExecutionMode(options, ORT_SEQUENTIAL));
    check(api->DisableMemPattern(options));
    check(api->SetSessionGraphOptimizationLevel(options, ORT_ENABLE_ALL));
    check(api->AddSessionConfigEntry(options, "session.intra_op.allow_spinning", "0"));
    check(api->AddFreeDimensionOverrideByName(options, "batch_size", 1));
    check(api->AddFreeDimensionOverrideByName(options, "height", height));
    check(api->AddFreeDimensionOverrideByName(options, "width", width));
    check(dmlApi->SessionOptionsAppendExecutionProvider_DML1(options, dml, queue));
    check(api->CreateSession(env, (directory + modelFile).c_str(), options, &session));
    check(api->CreateMemoryInfo("DML", OrtDeviceAllocator, 0, OrtMemTypeDefault, &memory));

    OrtAllocator* allocator = nullptr;
    check(api->GetAllocatorWithDefaultOptions(&allocator));
    size_t inputs = 0, outputs = 0;
    check(api->SessionGetInputCount(session, &inputs));
    check(api->SessionGetOutputCount(session, &outputs));
    if (inputs != 1 || outputs != 1)
        throw std::runtime_error("The model does not take one image and return one depth map.");
    char* name = nullptr;
    check(api->SessionGetInputName(session, 0, allocator, &name));
    inputName = name;
    check(api->AllocatorFree(allocator, name));
    check(api->SessionGetOutputName(session, 0, allocator, &name));
    outputName = name;
    check(api->AllocatorFree(allocator, name));

    OrtTypeInfo* info = nullptr;
    check(api->SessionGetInputTypeInfo(session, 0, &info));
    halfInput = IsHalf(ReadTensorInfo(api, info).type, "input");
    check(api->SessionGetOutputTypeInfo(session, 0, &info));
    const TensorInfo output = ReadTensorInfo(api, info);
    halfOutput = IsHalf(output.type, "output");
    inputShape = { 1, 3, height, width };
    if (output.rank == 3)
        outputShape = { 1, height, width };
    else if (output.rank == 4)
        outputShape = { 1, 1, height, width };
    else
        throw std::runtime_error("The model returns a depth map of an unexpected shape.");
}

void DepthModel::Unbind()
{
    for (OrtValue** value : { &inputValue, &outputValue })
        if (*value)
            api->ReleaseValue(std::exchange(*value, nullptr));
    for (void** allocation : { &inputAllocation, &outputAllocation })
        if (*allocation)
            dmlApi->FreeGPUAllocation(std::exchange(*allocation, nullptr));
    for (ID3D12Resource** buffer : { &inputBuffer, &outputBuffer })
        if (*buffer)
            std::exchange(*buffer, nullptr)->Release();
}

void DepthModel::Bind(ID3D12Resource* input, ID3D12Resource* output)
{
    Unbind();
    inputBuffer = input;
    outputBuffer = output;
    input->AddRef();
    output->AddRef();
    const OrtError check{ api };
    const auto value = [&](ID3D12Resource* buffer, void*& allocation, const std::vector<int64_t>& shape, bool half, OrtValue*& tensor) {
        check(dmlApi->CreateGPUAllocationFromD3DResource(buffer, &allocation));
        size_t count = 1;
        for (int64_t dimension : shape)
            count *= static_cast<size_t>(dimension);
        check(api->CreateTensorWithDataAsOrtValue(memory, allocation, count * (half ? 2 : 4), shape.data(), shape.size(),
                                                  half ? ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT16 : ONNX_TENSOR_ELEMENT_DATA_TYPE_FLOAT, &tensor));
    };
    value(input, inputAllocation, inputShape, halfInput, inputValue);
    value(output, outputAllocation, outputShape, halfOutput, outputValue);
}

void DepthModel::Run()
{
    if (!inputValue || !outputValue)
        throw std::logic_error("The depth model has no buffers to run on.");
    const char* inNames[] = { inputName.c_str() };
    const char* outNames[] = { outputName.c_str() };
    OrtError{ api }(api->Run(session, nullptr, inNames, &inputValue, 1, outNames, 1, &outputValue));
}
