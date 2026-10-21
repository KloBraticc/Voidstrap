#pragma once

#include <cstdint>
#include <string>
#include <vector>

struct ID3D12Device;
struct ID3D12CommandQueue;
struct ID3D12Resource;
struct IDMLDevice;
struct OrtApi;
struct OrtDmlApi;
struct OrtEnv;
struct OrtSessionOptions;
struct OrtSession;
struct OrtMemoryInfo;
struct OrtValue;

class DepthModel
{
public:
    ~DepthModel();

    void Load(const std::wstring& directory, const std::wstring& modelFile, int width, int height, ID3D12Device* device);

    bool HalfInput() const { return halfInput; }
    bool HalfOutput() const { return halfOutput; }
    ID3D12CommandQueue* Queue() const { return queue; }

    void Bind(ID3D12Resource* input, ID3D12Resource* output);

    void Run();

private:
    void Unbind();

    void* library = nullptr;
    void* dmlLibrary = nullptr;
    IDMLDevice* dml = nullptr;
    ID3D12CommandQueue* queue = nullptr;
    const OrtApi* api = nullptr;
    const OrtDmlApi* dmlApi = nullptr;
    OrtEnv* env = nullptr;
    OrtSessionOptions* options = nullptr;
    OrtSession* session = nullptr;
    OrtMemoryInfo* memory = nullptr;
    std::string inputName;
    std::string outputName;
    bool halfInput = false;
    bool halfOutput = false;
    std::vector<int64_t> inputShape;
    std::vector<int64_t> outputShape;
    ID3D12Resource* inputBuffer = nullptr;
    ID3D12Resource* outputBuffer = nullptr;
    void* inputAllocation = nullptr;
    void* outputAllocation = nullptr;
    OrtValue* inputValue = nullptr;
    OrtValue* outputValue = nullptr;
};
