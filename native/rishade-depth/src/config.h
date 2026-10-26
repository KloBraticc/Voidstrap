#pragma once

#include <string>

inline constexpr int kDepthSizes[] = { 924, 518, 392, 266 };
constexpr int kDefaultDepthSize = 266;

int DepthSize();
void SetDepthSize(int size);
std::wstring ExeDirectory();
std::wstring ModelFile();
std::wstring FastModelFile();
bool ExactDepth();
