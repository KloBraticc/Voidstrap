#pragma once

#include <unknwn.h>
#include <windows.h>
#include <d3d11_4.h>

#include <winrt/base.h>

#include <chrono>

struct FrameStatistics
{
    using Clock = std::chrono::steady_clock;

    void RecordDepth(double, double)
    {
    }
};

struct State
{
    winrt::com_ptr<ID3D11Device> device;
    winrt::com_ptr<ID3D11DeviceContext> context;
    FrameStatistics frameStatistics;
};

extern State& g;
