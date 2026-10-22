#include "depth.h"
#include "../addon.h"
#include "../capture.h"
#include "../config.h"
#include "../log.h"
#include "../reshade_imgui.h"
#include "../state.h"

#include <d3dcompiler.h>

#include <algorithm>
#include <array>
#include <cmath>
#include <cstring>
#include <optional>
#include <stdexcept>
#include <vector>

namespace
{
constexpr char kShaders[] = R"(
cbuffer Sizes : register(b0)
{
    uint2 SourceSize;
    uint2 WorkSize;
    float Temporal;
    float3 Padding;
};
Texture2D<float4> Source : register(t0);
Texture2D<float4> Guide : register(t1);
StructuredBuffer<uint> SkyEnd : register(t2);
StructuredBuffer<float4> Scene : register(t3);
Texture2D<float> WorkDepthView : register(t4);
Texture2D<float4> Blurred : register(t5);
RWTexture2D<float4> GuideTarget : register(u0);
RWStructuredBuffer<uint> SkyEndTarget : register(u1);
RWStructuredBuffer<float4> SceneTarget : register(u2);
RWTexture2D<float> WorkDepth : register(u3);
RWTexture2D<float> Depth : register(u4);
RWTexture2D<float4> BlurredTarget : register(u5);

static const float FarPlane = 1000.0;
static const float Nearest = 0.01;
static const float NearGround = 0.035;
static const float InterfaceBand = 0.07;
static const float SkyEdge = 0.035;
static const float SkyDrift = 0.35;
static const float ObjectEdge = 0.06;
static const float Relax = 0.12;
static const float HazeReach = 0.5;
static const float HazeWeight = 0.7;
static const float HazeLimit = 3.0;
static const int BlurRadius = 3;
static const float SpatialSigma = 1.4;
static const float ColorSigma = 0.18;

[numthreads(16, 16, 1)]
void Reduce(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    uint2 begin = id.xy * SourceSize / WorkSize;
    uint2 end = max((id.xy + 1) * SourceSize / WorkSize, begin + 1);
    float3 sum = 0;
    for (uint y = begin.y; y < end.y; ++y)
        for (uint x = begin.x; x < end.x; ++x)
            sum += saturate(Source.Load(int3(x, y, 0)).rgb);
    GuideTarget[id.xy] = float4(sum / ((end.x - begin.x) * (end.y - begin.y)), 1);
}

[numthreads(16, 16, 1)]
void Blur(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= WorkSize))
        return;
    float3 sum = 0;
    for (int y = -BlurRadius; y <= BlurRadius; ++y)
        for (int x = -BlurRadius; x <= BlurRadius; ++x)
            sum += Guide.Load(int3(clamp(int2(id.xy) + int2(x, y), 0, int2(WorkSize) - 1), 0)).rgb;
    BlurredTarget[id.xy] = float4(sum / ((2 * BlurRadius + 1) * (2 * BlurRadius + 1)), 1);
}

[numthreads(64, 1, 1)]
void Sky(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= WorkSize.x)
        return;
    uint start = uint(WorkSize.y * InterfaceBand);
    float3 mean = Blurred.Load(int3(id.x, start, 0)).rgb;
    float3 previous = mean;
    uint y = start + 1;
    for (; y < WorkSize.y; ++y)
    {
        float3 color = Blurred.Load(int3(id.x, y, 0)).rgb;
        if (length(color - previous) > SkyEdge || length(color - mean) > SkyDrift)
            break;
        mean += (color - mean) / (y - start + 1);
        previous = color;
    }
    SkyEndTarget[id.x] = y >= start + 3 ? y : 0;
}

groupshared uint Histogram[256];
groupshared float4 Totals[256];

[numthreads(256, 1, 1)]
void Horizon(uint3 id : SV_DispatchThreadID, uint thread : SV_GroupIndex)
{
    Histogram[thread] = 0;
    GroupMemoryBarrierWithGroupSync();
    float4 total = 0;
    for (uint x = thread; x < WorkSize.x; x += 256)
    {
        uint end = SkyEnd[x];
        if (end == 0)
            continue;
        uint bin = min(end * 256 / WorkSize.y, 255);
        InterlockedAdd(Histogram[bin], 1);
        total += float4(1, Blurred.Load(int3(x, end / 2, 0)).rgb);
    }
    Totals[thread] = total;
    GroupMemoryBarrierWithGroupSync();
    for (uint step = 128; step > 0; step >>= 1)
    {
        if (thread < step)
            Totals[thread] += Totals[thread + step];
        GroupMemoryBarrierWithGroupSync();
    }
    if (thread != 0)
        return;
    float skyColumns = Totals[0].x;
    float4 previous = Scene[0];
    float4 previousSky = Scene[1];
    float horizon;
    float3 skyColor;
    if (skyColumns >= WorkSize.x * 0.15)
    {
        uint half = uint(skyColumns) / 2;
        uint seen = 0;
        uint bin = 0;
        for (; bin < 255; ++bin)
        {
            seen += Histogram[bin];
            if (seen > half)
                break;
        }
        horizon = (bin + 0.5) * WorkSize.y / 256;
        skyColor = Totals[0].yzw / skyColumns;
    }
    else
    {
        horizon = -0.6 * WorkSize.y;
        skyColor = previousSky.w > 0 ? previousSky.rgb : float3(0.62, 0.72, 0.85);
    }
    if (previous.w > 0)
    {
        horizon = lerp(previous.x, horizon, 0.15);
        skyColor = lerp(previousSky.rgb, skyColor, 0.15);
    }
    SceneTarget[0] = float4(horizon, 0, 0, 1);
    SceneTarget[1] = float4(skyColor, 1);
}

[numthreads(64, 1, 1)]
void Columns(uint3 id : SV_DispatchThreadID)
{
    if (id.x >= WorkSize.x)
        return;
    float horizon = Scene[0].x;
    float3 skyColor = Scene[1].rgb;
    float bottom = max((WorkSize.y - horizon) / WorkSize.y, 0.05);
    float scale = NearGround * bottom;
    uint skyEnd = SkyEnd[id.x];
    float carried = 1;
    float3 below = 0;
    for (int y = int(WorkSize.y) - 1; y >= 0; --y)
    {
        float3 color = Blurred.Load(int3(id.x, y, 0)).rgb;
        float distance;
        if (uint(y) < skyEnd)
        {
            distance = 1;
        }
        else
        {
            float height = (y + 0.5 - horizon) / WorkSize.y;
            float ground = height > 0.002 ? clamp(scale / height, Nearest, 1) : 1;
            float haze = saturate(1 - length(color - skyColor) / HazeReach);
            float estimate = max(ground, min(haze * haze * HazeWeight, ground * HazeLimit));
            bool joined = y < int(WorkSize.y) - 1 && length(color - below) < ObjectEdge;
            distance = joined ? min(estimate, lerp(carried, estimate, Relax)) : estimate;
        }
        carried = distance;
        below = color;
        float previous = WorkDepth[uint2(id.x, y)];
        WorkDepth[uint2(id.x, y)] = previous > 0 ? lerp(previous, distance, Temporal) : distance;
    }
}

[numthreads(16, 16, 1)]
void Upsample(uint3 id : SV_DispatchThreadID)
{
    if (any(id.xy >= SourceSize))
        return;
    float3 color = saturate(Source.Load(int3(id.xy, 0)).rgb);
    float2 position = (id.xy + 0.5) * WorkSize / SourceSize - 0.5;
    int2 corner = int2(floor(position)) - 1;
    float sum = 0;
    float weights = 0;
    for (int y = 0; y < 4; ++y)
        for (int x = 0; x < 4; ++x)
        {
            int2 texel = corner + int2(x, y);
            float2 offset = texel - position;
            texel = clamp(texel, 0, int2(WorkSize) - 1);
            float3 difference = Guide.Load(int3(texel, 0)).rgb - color;
            float weight = exp(-dot(offset, offset) / (2 * SpatialSigma * SpatialSigma)) *
                           (exp(-dot(difference, difference) / (2 * ColorSigma * ColorSigma)) + 1e-3);
            sum += weight * WorkDepthView.Load(int3(texel, 0));
            weights += weight;
        }
    float distance01 = clamp(sum / weights, Nearest, 1);
    Depth[id.xy] = distance01 * FarPlane / (1 + distance01 * (FarPlane - 1));
}
)";

constexpr const char* kEntryPoints[] = { "Reduce", "Blur", "Sky", "Horizon", "Columns", "Upsample" };

enum Shader : size_t
{
    ReduceShader,
    BlurShader,
    SkyShader,
    HorizonShader,
    ColumnsShader,
    UpsampleShader,
};

constexpr float kTemporal = 0.55f;

struct Depth
{
    bool enabled = false;
    int size = 0;
    UINT workWidth = 0;
    UINT workHeight = 0;
    UINT sourceWidth = 0;
    UINT sourceHeight = 0;
    std::optional<int64_t> lastInput;
    std::array<winrt::com_ptr<ID3DBlob>, std::size(kEntryPoints)> code;
    std::array<winrt::com_ptr<ID3D11ComputeShader>, std::size(kEntryPoints)> shaders;
    winrt::com_ptr<ID3D11Buffer> sizes;
    winrt::com_ptr<ID3D11Texture2D> frameCopy;
    winrt::com_ptr<ID3D11ShaderResourceView> frameView;
    winrt::com_ptr<ID3D11Texture2D> guide;
    winrt::com_ptr<ID3D11UnorderedAccessView> guideTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> guideView;
    winrt::com_ptr<ID3D11Texture2D> blurred;
    winrt::com_ptr<ID3D11UnorderedAccessView> blurredTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> blurredView;
    winrt::com_ptr<ID3D11Buffer> skyEnd;
    winrt::com_ptr<ID3D11UnorderedAccessView> skyEndTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> skyEndView;
    winrt::com_ptr<ID3D11Buffer> scene;
    winrt::com_ptr<ID3D11UnorderedAccessView> sceneTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> sceneView;
    winrt::com_ptr<ID3D11Texture2D> workDepth;
    winrt::com_ptr<ID3D11UnorderedAccessView> workDepthTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> workDepthView;
    winrt::com_ptr<ID3D11Texture2D> texture;
    winrt::com_ptr<ID3D11UnorderedAccessView> textureTarget;
    winrt::com_ptr<ID3D11ShaderResourceView> view;
    std::vector<reshade::api::effect_runtime*> runtimes;
};
Depth d;

void Bind(reshade::api::effect_runtime* runtime, ID3D11ShaderResourceView* view)
{
    const reshade::api::resource_view handle{ reinterpret_cast<uint64_t>(view) };
    runtime->update_texture_bindings("DEPTH", handle, handle);
    runtime->enumerate_uniform_variables(nullptr, [ready = view != nullptr](reshade::api::effect_runtime* runtime, reshade::api::effect_uniform_variable variable) {
        char source[32];
        if (runtime->get_annotation_string_from_uniform_variable(variable, "source", source) && std::strcmp(source, "bufready_depth") == 0)
            runtime->set_uniform_value_bool(variable, ready);
    });
}

void OnInitRuntime(reshade::api::effect_runtime* runtime)
{
    d.runtimes.push_back(runtime);
    if (d.view)
        Bind(runtime, d.view.get());
}

void OnDestroyRuntime(reshade::api::effect_runtime* runtime)
{
    d.runtimes.erase(std::remove(d.runtimes.begin(), d.runtimes.end(), runtime), d.runtimes.end());
}

void OnReloadedEffects(reshade::api::effect_runtime* runtime)
{
    if (d.view)
        Bind(runtime, d.view.get());
}

void CreateShaders()
{
    for (size_t i = 0; i < std::size(kEntryPoints); ++i)
        winrt::check_hresult(g.device->CreateComputeShader(d.code[i]->GetBufferPointer(), d.code[i]->GetBufferSize(), nullptr, d.shaders[i].put()));

    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = 32;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
    winrt::check_hresult(g.device->CreateBuffer(&desc, nullptr, d.sizes.put()));
}

void CompileShaders()
{
    for (size_t i = 0; i < std::size(kEntryPoints); ++i)
    {
        winrt::com_ptr<ID3DBlob> errors;
        const HRESULT hr = D3DCompile(kShaders, sizeof(kShaders) - 1, "depth", nullptr, nullptr, kEntryPoints[i], "cs_5_0",
                                      D3DCOMPILE_OPTIMIZATION_LEVEL3, 0, d.code[i].put(), errors.put());
        if (FAILED(hr))
            throw std::runtime_error(errors ? static_cast<const char*>(errors->GetBufferPointer()) : "compute shaders are unavailable on this GPU");
    }
    CreateShaders();
}

void Dispatch(Shader shader, std::array<ID3D11ShaderResourceView*, 6> views, std::array<ID3D11UnorderedAccessView*, 6> targets, UINT x, UINT y)
{
    ID3D11Buffer* constants[] = { d.sizes.get() };
    g.context->CSSetShader(d.shaders[shader].get(), nullptr, 0);
    g.context->CSSetConstantBuffers(0, 1, constants);
    g.context->CSSetShaderResources(0, static_cast<UINT>(views.size()), views.data());
    g.context->CSSetUnorderedAccessViews(0, static_cast<UINT>(targets.size()), targets.data(), nullptr);
    g.context->Dispatch(x, y, 1);
    views.fill(nullptr);
    targets.fill(nullptr);
    g.context->CSSetShaderResources(0, static_cast<UINT>(views.size()), views.data());
    g.context->CSSetUnorderedAccessViews(0, static_cast<UINT>(targets.size()), targets.data(), nullptr);
}

void ReleaseTargets()
{
    for (auto* runtime : d.runtimes)
        Bind(runtime, nullptr);
    d.view = nullptr;
    d.textureTarget = nullptr;
    d.texture = nullptr;
    d.workDepthView = nullptr;
    d.workDepthTarget = nullptr;
    d.workDepth = nullptr;
    d.sceneView = nullptr;
    d.sceneTarget = nullptr;
    d.scene = nullptr;
    d.skyEndView = nullptr;
    d.skyEndTarget = nullptr;
    d.skyEnd = nullptr;
    d.blurredView = nullptr;
    d.blurredTarget = nullptr;
    d.blurred = nullptr;
    d.guideView = nullptr;
    d.guideTarget = nullptr;
    d.guide = nullptr;
}

void ReleaseResources()
{
    ReleaseTargets();
    d.frameView = nullptr;
    d.frameCopy = nullptr;
    d.sizes = nullptr;
    for (auto& shader : d.shaders)
        shader = nullptr;
    d.sourceWidth = 0;
    d.sourceHeight = 0;
    d.lastInput.reset();
}

void Stop()
{
    ReleaseResources();
    d.enabled = false;
    Report(LogLevel::Warning, L"Depth estimation stopped, color effects keep working.");
}

void CreateBuffer(UINT elements, UINT stride, winrt::com_ptr<ID3D11Buffer>& buffer, winrt::com_ptr<ID3D11UnorderedAccessView>& target, winrt::com_ptr<ID3D11ShaderResourceView>& view)
{
    D3D11_BUFFER_DESC desc{};
    desc.ByteWidth = elements * stride;
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_UNORDERED_ACCESS | D3D11_BIND_SHADER_RESOURCE;
    desc.MiscFlags = D3D11_RESOURCE_MISC_BUFFER_STRUCTURED;
    desc.StructureByteStride = stride;
    winrt::check_hresult(g.device->CreateBuffer(&desc, nullptr, buffer.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(buffer.get(), nullptr, target.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(buffer.get(), nullptr, view.put()));
}

void CreateTexture(UINT width, UINT height, DXGI_FORMAT format, winrt::com_ptr<ID3D11Texture2D>& texture, winrt::com_ptr<ID3D11UnorderedAccessView>& target, winrt::com_ptr<ID3D11ShaderResourceView>& view)
{
    D3D11_TEXTURE2D_DESC desc{};
    desc.Width = width;
    desc.Height = height;
    desc.MipLevels = 1;
    desc.ArraySize = 1;
    desc.Format = format;
    desc.SampleDesc = { 1, 0 };
    desc.Usage = D3D11_USAGE_DEFAULT;
    desc.BindFlags = D3D11_BIND_SHADER_RESOURCE | D3D11_BIND_UNORDERED_ACCESS;
    winrt::check_hresult(g.device->CreateTexture2D(&desc, nullptr, texture.put()));
    winrt::check_hresult(g.device->CreateUnorderedAccessView(texture.get(), nullptr, target.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(texture.get(), nullptr, view.put()));
}

void Resize(const D3D11_TEXTURE2D_DESC& frame)
{
    ReleaseTargets();
    d.size = DepthSize();
    const bool landscape = frame.Width >= frame.Height;
    const float ratio = landscape ? static_cast<float>(frame.Height) / frame.Width : static_cast<float>(frame.Width) / frame.Height;
    const UINT longSide = static_cast<UINT>(std::clamp(d.size, 64, 1024));
    const UINT shortSide = std::max(16u, static_cast<UINT>(std::lround(longSide * ratio)));
    d.workWidth = std::min(landscape ? longSide : shortSide, frame.Width);
    d.workHeight = std::min(landscape ? shortSide : longSide, frame.Height);
    d.sourceWidth = frame.Width;
    d.sourceHeight = frame.Height;
    d.lastInput.reset();
    Log(LogLevel::Info, L"Depth resize: source=%ux%u, work=%ux%u, detail=%d.", frame.Width, frame.Height, d.workWidth, d.workHeight, d.size);

    D3D11_TEXTURE2D_DESC copy = frame;
    copy.MipLevels = 1;
    copy.ArraySize = 1;
    copy.SampleDesc = { 1, 0 };
    copy.Usage = D3D11_USAGE_DEFAULT;
    copy.BindFlags = D3D11_BIND_SHADER_RESOURCE;
    copy.CPUAccessFlags = 0;
    copy.MiscFlags = 0;
    d.frameCopy = nullptr;
    d.frameView = nullptr;
    winrt::check_hresult(g.device->CreateTexture2D(&copy, nullptr, d.frameCopy.put()));
    winrt::check_hresult(g.device->CreateShaderResourceView(d.frameCopy.get(), nullptr, d.frameView.put()));

    CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R16G16B16A16_FLOAT, d.guide, d.guideTarget, d.guideView);
    CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R16G16B16A16_FLOAT, d.blurred, d.blurredTarget, d.blurredView);
    CreateTexture(d.workWidth, d.workHeight, DXGI_FORMAT_R32_FLOAT, d.workDepth, d.workDepthTarget, d.workDepthView);
    CreateTexture(d.sourceWidth, d.sourceHeight, DXGI_FORMAT_R32_FLOAT, d.texture, d.textureTarget, d.view);
    CreateBuffer(d.workWidth, sizeof(UINT), d.skyEnd, d.skyEndTarget, d.skyEndView);
    CreateBuffer(2, 4 * sizeof(float), d.scene, d.sceneTarget, d.sceneView);

    const float zero[4] = {};
    const UINT zeroUint[4] = {};
    g.context->ClearUnorderedAccessViewFloat(d.workDepthTarget.get(), zero);
    g.context->ClearUnorderedAccessViewUint(d.sceneTarget.get(), zeroUint);
    const float farPlane[4] = { 1, 1, 1, 1 };
    g.context->ClearUnorderedAccessViewFloat(d.textureTarget.get(), farPlane);

    struct
    {
        UINT sourceWidth, sourceHeight, workWidth, workHeight;
        float temporal, padding[3];
    } sizes{ d.sourceWidth, d.sourceHeight, d.workWidth, d.workHeight, kTemporal, {} };
    g.context->UpdateSubresource(d.sizes.get(), 0, nullptr, &sizes, 0, 0);
    for (auto* runtime : d.runtimes)
        Bind(runtime, d.view.get());
}

void Estimate()
{
    const UINT workX = (d.workWidth + 15) / 16;
    const UINT workY = (d.workHeight + 15) / 16;
    const UINT columns = (d.workWidth + 63) / 64;
    ID3D11ShaderResourceView* guide = d.guideView.get();
    ID3D11ShaderResourceView* blurred = d.blurredView.get();
    Dispatch(ReduceShader, { d.frameView.get() }, { d.guideTarget.get() }, workX, workY);
    Dispatch(BlurShader, { nullptr, guide }, { nullptr, nullptr, nullptr, nullptr, nullptr, d.blurredTarget.get() }, workX, workY);
    Dispatch(SkyShader, { nullptr, nullptr, nullptr, nullptr, nullptr, blurred }, { nullptr, d.skyEndTarget.get() }, columns, 1);
    Dispatch(HorizonShader, { nullptr, nullptr, d.skyEndView.get(), nullptr, nullptr, blurred }, { nullptr, nullptr, d.sceneTarget.get() }, 1, 1);
    Dispatch(ColumnsShader, { nullptr, nullptr, d.skyEndView.get(), d.sceneView.get(), nullptr, blurred }, { nullptr, nullptr, nullptr, d.workDepthTarget.get() }, columns, 1);
    Dispatch(UpsampleShader, { d.frameView.get(), guide, nullptr, nullptr, d.workDepthView.get() }, { nullptr, nullptr, nullptr, nullptr, d.textureTarget.get() },
             (d.sourceWidth + 15) / 16, (d.sourceHeight + 15) / 16);
}
}

bool InitDepth()
{
    if (!AddonRegistered())
    {
        Log(LogLevel::Warning, L"Depth estimation is off because ReShade did not load the depth add-on.");
        return false;
    }
    reshade::register_event<reshade::addon_event::init_effect_runtime>(OnInitRuntime);
    reshade::register_event<reshade::addon_event::destroy_effect_runtime>(OnDestroyRuntime);
    reshade::register_event<reshade::addon_event::reshade_reloaded_effects>(OnReloadedEffects);

    try
    {
        CompileShaders();
    }
    catch (const std::exception& e)
    {
        Log(LogLevel::Error, L"Depth estimation is off: %hs", e.what());
        return false;
    }
    d.enabled = true;
    Log(LogLevel::Info, L"Scene depth estimation initialized, depth_size=%d.", DepthSize());
    return true;
}

void UpdateDepth(ID3D11Texture2D* frame, int64_t frameTimestamp)
{
    if (!d.enabled)
        return;
    try
    {
        if (!d.shaders[0])
            CreateShaders();
        D3D11_TEXTURE2D_DESC desc{};
        frame->GetDesc(&desc);
        if (desc.Width != d.sourceWidth || desc.Height != d.sourceHeight || d.size != DepthSize() || !d.texture)
            Resize(desc);
        if (d.lastInput && *d.lastInput == frameTimestamp)
            return;
        g.context->CopyResource(d.frameCopy.get(), frame);
        Estimate();
        d.lastInput = frameTimestamp;
    }
    catch (const winrt::hresult_error& e)
    {
        if (DeviceLost(e.code()))
            throw;
        Log(LogLevel::Error, L"Depth estimation stopped: %ls (0x%08X)", e.message().c_str(), static_cast<unsigned>(e.code()));
        Stop();
    }
}

bool DepthEnabled()
{
    return d.enabled;
}

void ResetDepthInput()
{
    d.lastInput.reset();
}

void LogDepthDiagnostics()
{
    Log(LogLevel::Info, L"Depth state: enabled=%d, source=%ux%u, work=%ux%u.", d.enabled, d.sourceWidth, d.sourceHeight, d.workWidth, d.workHeight);
}

void ReleaseDepthDevice()
{
    ReleaseResources();
}

void ShutdownDepth()
{
    ReleaseResources();
    d.enabled = false;
}
