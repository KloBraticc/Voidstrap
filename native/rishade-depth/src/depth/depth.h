#pragma once

#include <d3d11.h>
#include <cstdint>
#include <reshade.hpp>

bool InitDepth();

void UpdateDepth(ID3D11Texture2D* frame, int64_t frameTimestamp);

void ResetDepthInput();
void LogDepthDiagnostics();

bool DepthEnabled();

void ReleaseDepthDevice();

void ShutdownDepth();

void AdoptDepthRuntime(reshade::api::effect_runtime* runtime);
