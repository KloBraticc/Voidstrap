#pragma once

#include <d3d11.h>
#include <cstdint>
#include <reshade.hpp>

bool SceneInitDepth();

void SceneUpdateDepth(ID3D11Texture2D* frame, int64_t frameTimestamp);

void SceneResetDepthInput();
void SceneLogDepthDiagnostics();

bool SceneDepthEnabled();

void SceneReleaseDepthDevice();

void SceneShutdownDepth();

void SceneAdoptDepthRuntime(reshade::api::effect_runtime* runtime);
