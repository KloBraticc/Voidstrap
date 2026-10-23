#include "addon.h"
#include "capture.h"
#include "config.h"
#include "log.h"
#include "reshade_imgui.h"
#include "state.h"
#include "depth/depth.h"
#include "depth/scene_depth.h"

#include <cstdarg>
#include <cstdint>
#include <cwchar>
#include <string>
#include <algorithm>
#include <vector>

State g;

extern "C" __declspec(dllexport) const char* NAME = "RiShade Depth";
extern "C" __declspec(dllexport) const char* DESCRIPTION = "Supplies estimated depth to ReShade effects in Voidstrap.";

namespace
{
HMODULE module = nullptr;
std::wstring directory;
std::wstring modelFile;
bool usingModel = false;
int depthSize = kDefaultDepthSize;
bool registered = false;
std::vector<reshade::api::effect_runtime*> liveRuntimes;

void OnBridgeInitRuntime(reshade::api::effect_runtime* runtime)
{
    liveRuntimes.push_back(runtime);
}

void OnBridgeDestroyRuntime(reshade::api::effect_runtime* runtime)
{
    std::erase(liveRuntimes, runtime);
}
bool attempted = false;
bool initialized = false;

void Write(const wchar_t* format, va_list args)
{
    wchar_t message[1024];
    _vsnwprintf_s(message, _TRUNCATE, format, args);
    OutputDebugStringW(L"[RiShade Depth] ");
    OutputDebugStringW(message);
    OutputDebugStringW(L"\n");
}

bool Attach(ID3D11Device* device, ID3D11DeviceContext* context)
{
    if (!device || !context)
        return false;
    g.device.copy_from(device);
    g.context.copy_from(context);
    return true;
}
}

BOOL APIENTRY DllMain(HMODULE instance, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH)
    {
        module = instance;
        DisableThreadLibraryCalls(instance);
    }
    return TRUE;
}

bool AddonRegistered()
{
    return registered;
}

int DepthSize()
{
    return depthSize;
}

void SetDepthSize(int size)
{
    depthSize = size;
}

std::wstring ExeDirectory()
{
    return directory;
}

std::wstring ModelFile()
{
    return modelFile;
}

namespace
{
bool StartScene()
{
    usingModel = false;
    if (!SceneInitDepth())
        return false;
    for (auto* runtime : liveRuntimes)
        SceneAdoptDepthRuntime(runtime);
    return true;
}

bool StartModel()
{
    if (modelFile.empty() || !InitDepth())
        return false;
    usingModel = true;
    for (auto* runtime : liveRuntimes)
        AdoptDepthRuntime(runtime);
    return true;
}

bool ActiveEnabled()
{
    return usingModel ? DepthEnabled() : SceneDepthEnabled();
}

void ActiveRelease()
{
    if (usingModel)
        ReleaseDepthDevice();
    else
        SceneReleaseDepthDevice();
}

void ActiveShutdown()
{
    if (usingModel)
        ShutdownDepth();
    else
        SceneShutdownDepth();
}
}

extern "C" __declspec(dllexport) void RiShadeDepthBridgeSetModel(const wchar_t* file)
{
    modelFile = file ? file : L"";
}

extern "C" __declspec(dllexport) bool RiShadeDepthBridgeReload(const wchar_t* file)
{
    modelFile = file ? file : L"";
    if (!initialized)
        return false;
    try
    {
        ActiveRelease();
        ActiveShutdown();
        initialized = StartModel() || StartScene();
    }
    catch (...)
    {
        initialized = false;
        usingModel = false;
    }
    Log(LogLevel::Info, usingModel ? L"Depth switched to the AI model %ls" : L"Depth switched to scene estimation", modelFile.c_str());
    return initialized;
}

extern "C" __declspec(dllexport) bool RiShadeDepthBridgeAttachRuntime(reshade::api::effect_runtime* runtime)
{
    if (!initialized || !runtime)
        return false;
    try
    {
        if (std::find(liveRuntimes.begin(), liveRuntimes.end(), runtime) == liveRuntimes.end())
            liveRuntimes.push_back(runtime);
        if (usingModel)
            AdoptDepthRuntime(runtime);
        else
            SceneAdoptDepthRuntime(runtime);
        return true;
    }
    catch (...)
    {
        return false;
    }
}

extern "C" __declspec(dllexport) bool RiShadeDepthBridgeUsingModel()
{
    return initialized && usingModel;
}

void Log(LogLevel, const wchar_t* format, ...)
{
    va_list args;
    va_start(args, format);
    Write(format, args);
    va_end(args);
}

void Report(LogLevel, const wchar_t* format, ...)
{
    va_list args;
    va_start(args, format);
    Write(format, args);
    va_end(args);
}

void LogStackTrace()
{
}

void InitThreadLog()
{
}

bool DeviceLost(HRESULT error)
{
    return error == DXGI_ERROR_DEVICE_REMOVED || error == DXGI_ERROR_DEVICE_RESET || error == DXGI_ERROR_DEVICE_HUNG ||
           (g.device && FAILED(g.device->GetDeviceRemovedReason()));
}

extern "C" __declspec(dllexport) bool RiShadeDepthBridgeInitialize(ID3D11Device* device, ID3D11DeviceContext* context, const wchar_t* modelDirectory)
{
    if (!modelDirectory || !Attach(device, context))
        return false;
    if (attempted)
        return initialized;
    attempted = true;
    try
    {
        directory = modelDirectory;
        if (!directory.empty() && directory.back() != L'\\' && directory.back() != L'/')
            directory += L'\\';
        registered = reshade::register_addon(module);
        if (registered)
        {
            reshade::register_event<reshade::addon_event::init_effect_runtime>(OnBridgeInitRuntime);
            reshade::register_event<reshade::addon_event::destroy_effect_runtime>(OnBridgeDestroyRuntime);
            initialized = StartModel() || StartScene();
        }
    }
    catch (...)
    {
        initialized = false;
    }
    return initialized;
}

extern "C" __declspec(dllexport) bool RiShadeDepthBridgeUpdate(ID3D11ShaderResourceView* source, int64_t timestamp)
{
    if (!initialized || !source || !g.device)
        return false;
    try
    {
        winrt::com_ptr<ID3D11Resource> resource;
        source->GetResource(resource.put());
        const auto texture = resource.try_as<ID3D11Texture2D>();
        if (!texture)
            return false;
        if (usingModel)
        {
            UpdateDepth(texture.get(), timestamp);
            if (DepthEnabled())
                return true;
            Log(LogLevel::Warning, L"The depth model stopped, switching to scene depth estimation");
            ReleaseDepthDevice();
            ShutdownDepth();
            if (!StartScene())
            {
                initialized = false;
                return false;
            }
        }
        SceneUpdateDepth(texture.get(), timestamp);
        return SceneDepthEnabled();
    }
    catch (...)
    {
        ActiveRelease();
        return false;
    }
}

extern "C" __declspec(dllexport) bool RiShadeDepthBridgeSetDevice(ID3D11Device* device, ID3D11DeviceContext* context)
{
    return initialized && Attach(device, context);
}

extern "C" __declspec(dllexport) void RiShadeDepthBridgeReleaseDevice()
{
    if (initialized)
        ActiveRelease();
    g.context = nullptr;
    g.device = nullptr;
}

extern "C" __declspec(dllexport) void RiShadeDepthBridgeShutdown()
{
    if (initialized)
    {
        ActiveRelease();
        ActiveShutdown();
        initialized = false;
        usingModel = false;
    }
    g.context = nullptr;
    g.device = nullptr;
    if (registered)
    {
        reshade::unregister_event<reshade::addon_event::init_effect_runtime>(OnBridgeInitRuntime);
        reshade::unregister_event<reshade::addon_event::destroy_effect_runtime>(OnBridgeDestroyRuntime);
        liveRuntimes.clear();
        reshade::unregister_addon(module);
        registered = false;
    }
    attempted = false;
}
