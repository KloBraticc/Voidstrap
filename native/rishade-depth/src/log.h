#pragma once

enum class LogLevel
{
    Info,
    Ok,
    Warning,
    Error,
};

void Log(LogLevel level, const wchar_t* format, ...);
void Report(LogLevel level, const wchar_t* format, ...);
void LogStackTrace();
void InitThreadLog();
