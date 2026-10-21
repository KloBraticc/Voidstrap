#include "addon.h"
#include "capture.h"
#include "config.h"
#include "log.h"
#include "reshade_imgui.h"
#include "state.h"
#include "depth/depth.h"

#include <cstdarg>
#include <cstdint>
#include <cwchar>
#include <string>

State g;

extern "C" __declspec(dllexport) const char* NAME = "RiShade Depth";
extern "C" __declspec(dllexport) const char* DESCRIPTION = "Supplies estimated depth to ReShade effects in Voidstrap.";

namespace
{
HMODULE module = nullptr;
std::wstring directory;
int depthSize = kDefaultDepthSize;
bool registered = false;
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
        initialized = registered && InitDepth();
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
        UpdateDepth(texture.get(), timestamp);
        return DepthEnabled();
    }
    catch (...)
    {
        ReleaseDepthDevice();
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
        ReleaseDepthDevice();
    g.context = nullptr;
    g.device = nullptr;
}

extern "C" __declspec(dllexport) void RiShadeDepthBridgeShutdown()
{
    if (initialized)
    {
        ReleaseDepthDevice();
        ShutdownDepth();
        initialized = false;
    }
    g.context = nullptr;
    g.device = nullptr;
    if (registered)
    {
        reshade::unregister_addon(module);
        registered = false;
    }
}
