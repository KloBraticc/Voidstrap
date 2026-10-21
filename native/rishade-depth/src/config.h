#pragma once

#include <string>

inline constexpr int kDepthSizes[] = { 924, 518, 392, 266 };
constexpr int kDefaultDepthSize = 518;

int DepthSize();
void SetDepthSize(int size);
std::wstring ExeDirectory();
