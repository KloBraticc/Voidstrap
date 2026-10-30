using System;

namespace Voidstrap.Integrations.Plugins;

internal static partial class ModLibraryReview
{
    private const string RulesJson = """
{
  "limits": {
    "maxModBytes": 1048576,
    "maxFiles": 64,
    "maxFileBytes": 262144,
    "maxIconBytes": 262144,
    "maxPathDepth": 4,
    "maxLineLength": 400,
    "maxDescription": 300,
    "maxEncodedRun": 200
  },
  "allowedExtensions": [
    ".ts",
    ".tsx",
    ".js",
    ".mjs",
    ".jsx",
    ".json",
    ".md",
    ".ico"
  ],
  "codeExtensions": [
    ".ts",
    ".tsx",
    ".js",
    ".mjs",
    ".jsx"
  ],
  "blocked": [
    {
      "pattern": "\\beval\\s*\\(",
      "reason": "eval runs hidden code",
      "level": "error"
    },
    {
      "pattern": "\\bnew\\s+Function\\s*\\(|(?<![.\\w])Function\\s*\\(",
      "reason": "Function() builds code from text",
      "level": "error"
    },
    {
      "pattern": "\\bimport\\s*\\(\\s*[^'\"`\\s]",
      "reason": "dynamic import with a computed path",
      "level": "error"
    },
    {
      "pattern": "\\b(atob|btoa|unescape|decodeURIComponent)\\s*\\(",
      "reason": "decoding helpers are used to hide payloads",
      "level": "error"
    },
    {
      "pattern": "String\\.fromCharCode\\s*\\(",
      "reason": "fromCharCode is used to hide strings",
      "level": "error"
    },
    {
      "pattern": "(\\\\x[0-9a-fA-F]{2}){6,}|(\\\\u[0-9a-fA-F]{4}){6,}",
      "reason": "escaped character runs hide text",
      "level": "error"
    },
    {
      "pattern": "ROBLOSECURITY|robloxcookie|RobloxCookies\\.dat|\\bdocument\\.cookie\\b|set-cookie",
      "reason": "mods never read the Roblox login cookie: add the roblox-account permission and use $.roblox.request, Voidstrap attaches the login itself so it can never leak",
      "level": "error"
    },
    {
      "pattern": "\\b(password|passwd|auth[_-]?token|access[_-]?token|refresh[_-]?token|session[_-]?token|api[_-]?key|webhook)\\b",
      "reason": "credential or webhook handling needs manual review",
      "level": "review"
    },
    {
      "pattern": "discord(app)?\\.com/api/webhooks",
      "reason": "Discord webhooks are a common way to steal data",
      "level": "error",
      "binary": false
    },
    {
      "pattern": "\\b(getgenv|getrenv|getreg|hookfunction|hookmetamethod|newcclosure|setreadonly|getrawmetatable|loadstring|firetouchinterest|fireclickdetector|syn\\.request|identifyexecutor)\\b",
      "reason": "Roblox exploit and executor functions are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(synapse|krnl|fluxus|electron executor|script-?ware|delta executor|wave executor|solara|xeno executor|codex executor|arceus)\\b",
      "reason": "Roblox executors and cheats are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(aimbot|wallhack|esp hack|speedhack|fly ?hack|noclip|auto ?farm|triggerbot|silent ?aim|anti ?ban|byfron bypass|hyperion bypass)\\b",
      "reason": "cheats are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(WriteProcessMemory|ReadProcessMemory|VirtualAllocEx|CreateRemoteThread|OpenProcess|NtWriteVirtualMemory|LoadLibrary|DllImport|LibraryImport)\\b",
      "reason": "process memory and injection functions are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(child_process|require\\s*\\(|process\\.env|__dirname|XMLHttpRequest|WebSocket|importScripts)\\b",
      "reason": "Node and browser escape hatches are not part of the Voidstrap mod API",
      "level": "error",
      "binary": false
    },
    {
      "pattern": "\\b(crypto ?miner|coinhive|xmrig|monero)\\b",
      "reason": "crypto miners are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(\\u0070\\u006f\\u0072\\u006e\\u0068\\u0075\\u0062|\\u0078\\u0076\\u0069\\u0064\\u0065\\u006f\\u0073|\\u0078\\u006e\\u0078\\u0078|\\u0078\\u0068\\u0061\\u006d\\u0073\\u0074\\u0065\\u0072|\\u0072\\u0065\\u0064\\u0074\\u0075\\u0062\\u0065|\\u0079\\u006f\\u0075\\u0070\\u006f\\u0072\\u006e|\\u0073\\u0070\\u0061\\u006e\\u006b\\u0062\\u0061\\u006e\\u0067|\\u0065\\u0070\\u006f\\u0072\\u006e\\u0065\\u0072|\\u0062\\u0072\\u0061\\u007a\\u007a\\u0065\\u0072\\u0073|\\u006f\\u006e\\u006c\\u0079\\u0066\\u0061\\u006e\\u0073|\\u0066\\u0061\\u006e\\u0073\\u006c\\u0079|\\u0063\\u0068\\u0061\\u0074\\u0075\\u0072\\u0062\\u0061\\u0074\\u0065|\\u0073\\u0074\\u0072\\u0069\\u0070\\u0063\\u0068\\u0061\\u0074|\\u0063\\u0061\\u006d\\u0034|\\u0062\\u006f\\u006e\\u0067\\u0061\\u0063\\u0061\\u006d\\u0073|\\u006c\\u0069\\u0076\\u0065\\u006a\\u0061\\u0073\\u006d\\u0069\\u006e|\\u0072\\u0075\\u006c\\u0065\\u0033\\u0034|\\u0072\\u0033\\u0034|\\u0065\\u0036\\u0032\\u0031|\\u0067\\u0065\\u006c\\u0062\\u006f\\u006f\\u0072\\u0075|\\u0064\\u0061\\u006e\\u0062\\u006f\\u006f\\u0072\\u0075|\\u0073\\u0061\\u006e\\u006b\\u0061\\u006b\\u0075\\u0063\\u006f\\u006d\\u0070\\u006c\\u0065\\u0078|\\u006e\\u0068\\u0065\\u006e\\u0074\\u0061\\u0069|\\u0068\\u0061\\u006e\\u0069\\u006d\\u0065|\\u0068\\u0065\\u006e\\u0074\\u0061\\u0069\\u0068\\u0061\\u0076\\u0065\\u006e|\\u0066\\u0039\\u0035\\u007a\\u006f\\u006e\\u0065|\\u006c\\u0069\\u0074\\u0065\\u0072\\u006f\\u0074\\u0069\\u0063\\u0061|\\u006d\\u006f\\u0074\\u0068\\u0065\\u0072\\u006c\\u0065\\u0073\\u0073|\\u0068\\u0065\\u0061\\u0076\\u0079-\\u0072|\\u0065\\u0066\\u0075\\u006b\\u0074)\\b",
      "reason": "adult, pornographic and NSFW websites or services are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(\\u0070\\u006f\\u0072\\u006e|\\u0070\\u006f\\u0072\\u006e\\u006f|\\u0070\\u006f\\u0072\\u006e\\u006f\\u0067\\u0072\\u0061\\u0070\\u0068\\u0079|\\u0070\\u006f\\u0072\\u006e\\u006f\\u0067\\u0072\\u0061\\u0070\\u0068\\u0069\\u0063|\\u0068\\u0065\\u006e\\u0074\\u0061\\u0069|\\u006e\\u0073\\u0066\\u0077|\\u0078\\u0078\\u0078|\\u0065\\u0072\\u006f\\u0067\\u0065|\\u0065\\u0063\\u0063\\u0068\\u0069|\\u006c\\u0065\\u0077\\u0064|\\u0061\\u0068\\u0065\\u0067\\u0061\\u006f|\\u0066\\u0075\\u0074\\u0061\\u006e\\u0061\\u0072\\u0069|\\u006c\\u006f\\u006c\\u0069|\\u006c\\u006f\\u006c\\u0069\\u0063\\u006f\\u006e|\\u0073\\u0068\\u006f\\u0074\\u0061|\\u0073\\u0068\\u006f\\u0074\\u0061\\u0063\\u006f\\u006e|\\u0070\\u0061\\u0069\\u007a\\u0075\\u0072\\u0069|\\u0062\\u0075\\u006b\\u006b\\u0061\\u006b\\u0065|\\u0067\\u0061\\u006e\\u0067\\u0062\\u0061\\u006e\\u0067|\\u0063\\u0072\\u0065\\u0061\\u006d\\u0070\\u0069\\u0065|\\u0062\\u006c\\u006f\\u0077\\u006a\\u006f\\u0062|\\u0068\\u0061\\u006e\\u0064\\u006a\\u006f\\u0062|\\u0066\\u006f\\u006f\\u0074\\u006a\\u006f\\u0062|\\u0074\\u0069\\u0074\\u006a\\u006f\\u0062|\\u0064\\u0065\\u0065\\u0070\\u0074\\u0068\\u0072\\u006f\\u0061\\u0074|\\u0064\\u0069\\u006c\\u0064\\u006f|\\u0076\\u0069\\u0062\\u0072\\u0061\\u0074\\u006f\\u0072|\\u0066\\u006c\\u0065\\u0073\\u0068\\u006c\\u0069\\u0067\\u0068\\u0074|\\u0062\\u0064\\u0073\\u006d|\\u0062\\u006f\\u006e\\u0064\\u0061\\u0067\\u0065|\\u0066\\u0065\\u006d\\u0064\\u006f\\u006d|\\u006d\\u0061\\u006c\\u0065\\u0064\\u006f\\u006d|\\u0073\\u0061\\u0064\\u006f\\u006d\\u0061\\u0073\\u006f\\u0063\\u0068\\u0069\\u0073\\u006d|\\u0063\\u0075\\u0063\\u006b\\u006f\\u006c\\u0064|\\u006d\\u0069\\u006c\\u0066|\\u0064\\u0069\\u006c\\u0066|\\u0067\\u0069\\u006c\\u0066|\\u0063\\u0061\\u006d\\u0067\\u0069\\u0072\\u006c|\\u0063\\u0061\\u006d\\u0077\\u0068\\u006f\\u0072\\u0065|\\u0070\\u0072\\u006f\\u0073\\u0074\\u0069\\u0074\\u0075\\u0074\\u0065|\\u0070\\u0072\\u006f\\u0073\\u0074\\u0069\\u0074\\u0075\\u0074\\u0069\\u006f\\u006e|\\u0062\\u0072\\u006f\\u0074\\u0068\\u0065\\u006c|\\u0073\\u0074\\u0072\\u0069\\u0070\\u0070\\u0065\\u0072|\\u006c\\u0061\\u0070\\u0064\\u0061\\u006e\\u0063\\u0065|\\u0073\\u0074\\u0072\\u0069\\u0070\\u0074\\u0065\\u0061\\u0073\\u0065|\\u006f\\u0072\\u0067\\u0061\\u0073\\u006d|\\u006d\\u0061\\u0073\\u0074\\u0075\\u0072\\u0062\\u0061\\u0074(\\u0065|\\u0069\\u006e\\u0067|\\u0069\\u006f\\u006e)|\\u0065\\u006a\\u0061\\u0063\\u0075\\u006c\\u0061\\u0074(\\u0065|\\u0069\\u006e\\u0067|\\u0069\\u006f\\u006e)|\\u0063\\u0075\\u006d\\u0073\\u0068\\u006f\\u0074|\\u0063\\u0075\\u006d\\u0073\\u006c\\u0075\\u0074|\\u0063\\u006f\\u0063\\u006b\\u0073\\u0075\\u0063\\u006b\\u0065\\u0072|\\u006d\\u006f\\u0074\\u0068\\u0065\\u0072\\u0066\\u0075\\u0063\\u006b\\u0065\\u0072|\\u0062\\u0075\\u0074\\u0074\\u0070\\u006c\\u0075\\u0067|\\u0072\\u0065\\u0064\\u0067\\u0069\\u0066\\u0073|\\u0062\\u006f\\u006f\\u0072\\u0075)\\b",
      "reason": "pornographic, sexually explicit and NSFW content is banned",
      "level": "error"
    },
    {
      "pattern": "\\b(\\u006e\\u0075\\u0064\\u0065\\u0073|\\u006e\\u0075\\u0064\\u0069\\u0074\\u0079|\\u0074\\u006f\\u0070\\u006c\\u0065\\u0073\\u0073|\\u0062\\u006f\\u0074\\u0074\\u006f\\u006d\\u006c\\u0065\\u0073\\u0073|\\u0070\\u0061\\u006e\\u0074\\u0069\\u0065\\u0073|\\u006c\\u0069\\u006e\\u0067\\u0065\\u0072\\u0069\\u0065|\\u0075\\u0070\\u0073\\u006b\\u0069\\u0072\\u0074|\\u0064\\u006f\\u0077\\u006e\\u0062\\u006c\\u006f\\u0075\\u0073\\u0065|\\u0063\\u0061\\u006d\\u0065\\u006c\\u0074\\u006f\\u0065|\\u006e\\u0069\\u0070\\u0070\\u006c\\u0065|\\u006e\\u0069\\u0070\\u0070\\u006c\\u0065\\u0073|\\u0061\\u0072\\u0065\\u006f\\u006c\\u0061|\\u0062\\u006f\\u006f\\u0062\\u0073|\\u0074\\u0069\\u0074\\u0074\\u0069\\u0065\\u0073|\\u0074\\u0069\\u0074\\u0073|\\u0063\\u006c\\u0069\\u0074\\u006f\\u0072\\u0069\\u0073|\\u0076\\u0061\\u0067\\u0069\\u006e\\u0061|\\u0076\\u0075\\u006c\\u0076\\u0061|\\u006c\\u0061\\u0062\\u0069\\u0061|\\u0070\\u0065\\u006e\\u0069\\u0073|\\u0070\\u0068\\u0061\\u006c\\u006c\\u0075\\u0073|\\u0073\\u0063\\u0072\\u006f\\u0074\\u0075\\u006d|\\u0074\\u0065\\u0073\\u0074\\u0069\\u0063\\u006c\\u0065|\\u0074\\u0065\\u0073\\u0074\\u0069\\u0063\\u006c\\u0065\\u0073|\\u0065\\u0072\\u0065\\u0063\\u0074\\u0069\\u006f\\u006e|\\u0062\\u006f\\u006e\\u0065\\u0072|\\u0066\\u006f\\u0072\\u0065\\u0073\\u006b\\u0069\\u006e|\\u0061\\u006e\\u0075\\u0073|\\u0062\\u0075\\u0074\\u0074\\u0068\\u006f\\u006c\\u0065|\\u0061\\u0073\\u0073\\u0068\\u006f\\u006c\\u0065|\\u0063\\u006f\\u006e\\u0064\\u006f\\u006d|\\u0076\\u0069\\u0061\\u0067\\u0072\\u0061|\\u0063\\u0069\\u0061\\u006c\\u0069\\u0073|\\u0068\\u006f\\u0072\\u006e\\u0079|\\u0073\\u006c\\u0075\\u0074|\\u0073\\u006c\\u0075\\u0074\\u0073|\\u0073\\u006c\\u0075\\u0074\\u0074\\u0079|\\u0077\\u0068\\u006f\\u0072\\u0065|\\u0077\\u0068\\u006f\\u0072\\u0065\\u0073|\\u0073\\u006b\\u0061\\u006e\\u006b|\\u0070\\u0072\\u006f\\u0073\\u0074\\u0069\\u0074\\u0075\\u0074\\u0065|\\u0068\\u006f\\u006f\\u006b\\u0065\\u0072|\\u0066\\u0065\\u0074\\u0069\\u0073\\u0068|\\u006b\\u0069\\u006e\\u006b|\\u0065\\u0072\\u006f\\u0074\\u0069\\u0063|\\u0065\\u0072\\u006f\\u0074\\u0069\\u0063\\u0061|\\u0073\\u0065\\u0078 ?\\u0074\\u0061\\u0070\\u0065|\\u0073\\u0065\\u0078 ?\\u0074\\u006f\\u0079|\\u0073\\u0065\\u0078 ?\\u0063\\u0061\\u006d|\\u0063\\u0079\\u0062\\u0065\\u0072 ?\\u0073\\u0065\\u0078|\\u0070\\u0068\\u006f\\u006e\\u0065 ?\\u0073\\u0065\\u0078|\\u0061\\u006e\\u0061\\u006c ?\\u0073\\u0065\\u0078|\\u006f\\u0072\\u0061\\u006c ?\\u0073\\u0065\\u0078)\\b",
      "reason": "sexually explicit or NSFW terms are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(\\u006e\\u0069\\u0067\\u0067\\u0065\\u0072|\\u006e\\u0069\\u0067\\u0067\\u0065\\u0072\\u0073|\\u006e\\u0069\\u0067\\u0067\\u0061|\\u006e\\u0069\\u0067\\u0067\\u0061\\u0073|\\u0066\\u0061\\u0067\\u0067\\u006f\\u0074|\\u0066\\u0061\\u0067\\u0067\\u006f\\u0074\\u0073|\\u0066\\u0061\\u0067|\\u0066\\u0061\\u0067\\u0073|\\u0064\\u0079\\u006b\\u0065|\\u0074\\u0072\\u0061\\u006e\\u006e\\u0079|\\u0074\\u0072\\u0061\\u006e\\u006e\\u0069\\u0065\\u0073|\\u0073\\u0068\\u0065\\u006d\\u0061\\u006c\\u0065|\\u0072\\u0065\\u0074\\u0061\\u0072\\u0064|\\u0072\\u0065\\u0074\\u0061\\u0072\\u0064\\u0073|\\u0072\\u0065\\u0074\\u0061\\u0072\\u0064\\u0065\\u0064|\\u006b\\u0069\\u006b\\u0065|\\u006b\\u0069\\u006b\\u0065\\u0073|\\u0073\\u0070\\u0069\\u0063|\\u0073\\u0070\\u0069\\u0063\\u0073|\\u0063\\u0068\\u0069\\u006e\\u006b|\\u0063\\u0068\\u0069\\u006e\\u006b\\u0073|\\u0067\\u006f\\u006f\\u006b|\\u0067\\u006f\\u006f\\u006b\\u0073|\\u0077\\u0065\\u0074\\u0062\\u0061\\u0063\\u006b|\\u0077\\u0065\\u0074\\u0062\\u0061\\u0063\\u006b\\u0073|\\u0062\\u0065\\u0061\\u006e\\u0065\\u0072|\\u0062\\u0065\\u0061\\u006e\\u0065\\u0072\\u0073|\\u0063\\u006f\\u006f\\u006e|\\u0063\\u006f\\u006f\\u006e\\u0073|\\u0072\\u0061\\u0067\\u0068\\u0065\\u0061\\u0064|\\u0074\\u006f\\u0077\\u0065\\u006c\\u0068\\u0065\\u0061\\u0064|\\u0063\\u0061\\u006d\\u0065\\u006c ?\\u006a\\u006f\\u0063\\u006b\\u0065\\u0079|\\u0077\\u0068\\u0069\\u0074\\u0065 ?\\u0070\\u006f\\u0077\\u0065\\u0072|\\u0068\\u0065\\u0069\\u006c ?\\u0068\\u0069\\u0074\\u006c\\u0065\\u0072|\\u0073\\u0069\\u0065\\u0067 ?\\u0068\\u0065\\u0069\\u006c|\\u006b\\u0075 ?\\u006b\\u006c\\u0075\\u0078 ?\\u006b\\u006c\\u0061\\u006e|\\u006b\\u006b\\u006b|\\u006e\\u0061\\u007a\\u0069|\\u006e\\u0061\\u007a\\u0069\\u0073|\\u006e\\u0065\\u006f-?\\u006e\\u0061\\u007a\\u0069)\\b",
      "reason": "hate speech and slurs are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(\\u0066\\u0075\\u0063\\u006b|\\u0066\\u0075\\u0063\\u006b\\u0069\\u006e\\u0067|\\u0066\\u0075\\u0063\\u006b\\u0065\\u0064|\\u0066\\u0075\\u0063\\u006b\\u0065\\u0072|\\u0066\\u0075\\u0063\\u006b\\u0065\\u0072\\u0073|\\u006d\\u006f\\u0074\\u0068\\u0065\\u0072\\u0066\\u0075\\u0063\\u006b\\u0065\\u0072|\\u006d\\u006f\\u0074\\u0068\\u0065\\u0072\\u0066\\u0075\\u0063\\u006b\\u0069\\u006e\\u0067|\\u0073\\u0068\\u0069\\u0074|\\u0073\\u0068\\u0069\\u0074\\u0074\\u0079|\\u0062\\u0075\\u006c\\u006c\\u0073\\u0068\\u0069\\u0074|\\u0068\\u006f\\u0072\\u0073\\u0065\\u0073\\u0068\\u0069\\u0074|\\u0064\\u0069\\u0070\\u0073\\u0068\\u0069\\u0074|\\u0064\\u006f\\u0067\\u0073\\u0068\\u0069\\u0074|\\u0062\\u0061\\u0074\\u0073\\u0068\\u0069\\u0074|\\u0062\\u0069\\u0074\\u0063\\u0068|\\u0062\\u0069\\u0074\\u0063\\u0068\\u0065\\u0073|\\u0062\\u0069\\u0074\\u0063\\u0068\\u0069\\u006e\\u0067|\\u0073\\u006f\\u006e ?\\u006f\\u0066 ?\\u0061 ?\\u0062\\u0069\\u0074\\u0063\\u0068|\\u0062\\u0061\\u0073\\u0074\\u0061\\u0072\\u0064|\\u0062\\u0061\\u0073\\u0074\\u0061\\u0072\\u0064\\u0073|\\u0063\\u0075\\u006e\\u0074|\\u0063\\u0075\\u006e\\u0074\\u0073|\\u0074\\u0077\\u0061\\u0074|\\u0074\\u0077\\u0061\\u0074\\u0073|\\u0070\\u0072\\u0069\\u0063\\u006b|\\u0070\\u0072\\u0069\\u0063\\u006b\\u0073|\\u0064\\u0069\\u0063\\u006b\\u0068\\u0065\\u0061\\u0064|\\u0064\\u0069\\u0063\\u006b\\u0068\\u0065\\u0061\\u0064\\u0073|\\u0063\\u006f\\u0063\\u006b|\\u0063\\u006f\\u0063\\u006b\\u0073|\\u0070\\u0075\\u0073\\u0073\\u0079|\\u0070\\u0075\\u0073\\u0073\\u0069\\u0065\\u0073|\\u0077\\u0061\\u006e\\u006b\\u0065\\u0072|\\u0077\\u0061\\u006e\\u006b\\u0065\\u0072\\u0073|\\u0061\\u0072\\u0073\\u0065\\u0068\\u006f\\u006c\\u0065|\\u0061\\u0073\\u0073\\u0068\\u0061\\u0074|\\u006a\\u0061\\u0063\\u006b\\u0061\\u0073\\u0073|\\u0064\\u0075\\u006d\\u0062\\u0061\\u0073\\u0073|\\u0066\\u0061\\u0074\\u0061\\u0073\\u0073|\\u0062\\u0061\\u0064\\u0061\\u0073\\u0073|\\u0064\\u006f\\u0075\\u0063\\u0068\\u0065\\u0062\\u0061\\u0067|\\u0073\\u0063\\u0075\\u006d\\u0062\\u0061\\u0067|\\u0073\\u0068\\u0069\\u0074\\u0068\\u0065\\u0061\\u0064|\\u0066\\u0075\\u0063\\u006b\\u0066\\u0061\\u0063\\u0065|\\u0073\\u0068\\u0069\\u0074\\u0066\\u0061\\u0063\\u0065|\\u0070\\u0069\\u0073\\u0073 ?\\u006f\\u0066\\u0066|\\u0066\\u0075\\u0063\\u006b ?\\u006f\\u0066\\u0066)\\b",
      "reason": "profanity and vulgar language are banned",
      "level": "error"
    },
    {
      "pattern": "\\b(\\u0063\\u0068\\u0069\\u006c\\u0064 ?\\u0070\\u006f\\u0072\\u006e|\\u0063\\u0070 ?\\u006c\\u0069\\u006e\\u006b\\u0073?|\\u0063\\u0073\\u0061\\u006d|\\u0070\\u0065\\u0064\\u006f\\u0070\\u0068\\u0069\\u006c\\u0065|\\u0070\\u0065\\u0064\\u006f\\u0070\\u0068\\u0069\\u006c\\u0069\\u0061|\\u0070\\u0065\\u0064\\u006f|\\u0072\\u0061\\u0070\\u0065|\\u0072\\u0061\\u0070\\u0069\\u0073\\u0074|\\u0072\\u0061\\u0070\\u0069\\u006e\\u0067|\\u006d\\u006f\\u006c\\u0065\\u0073\\u0074|\\u006d\\u006f\\u006c\\u0065\\u0073\\u0074\\u0065\\u0072|\\u006d\\u006f\\u006c\\u0065\\u0073\\u0074\\u0061\\u0074\\u0069\\u006f\\u006e|\\u0069\\u006e\\u0063\\u0065\\u0073\\u0074|\\u0062\\u0065\\u0073\\u0074\\u0069\\u0061\\u006c\\u0069\\u0074\\u0079|\\u007a\\u006f\\u006f\\u0070\\u0068\\u0069\\u006c\\u0069\\u0061|\\u006e\\u0065\\u0063\\u0072\\u006f\\u0070\\u0068\\u0069\\u006c\\u0069\\u0061|\\u0073\\u006e\\u0075\\u0066\\u0066 ?\\u0066\\u0069\\u006c\\u006d|\\u0067\\u006f\\u0072\\u0065 ?\\u0073\\u0069\\u0074\\u0065|\\u006c\\u0069\\u0076\\u0065\\u006c\\u0065\\u0061\\u006b|\\u0062\\u0065\\u0073\\u0074\\u0067\\u006f\\u0072\\u0065|\\u006b\\u0069\\u006c\\u006c ?\\u0079\\u006f\\u0075\\u0072\\u0073\\u0065\\u006c\\u0066|\\u006b\\u0079\\u0073|\\u0073\\u0075\\u0069\\u0063\\u0069\\u0064\\u0065 ?\\u006d\\u0065\\u0074\\u0068\\u006f\\u0064|\\u0073\\u0065\\u006c\\u0066 ?\\u0068\\u0061\\u0072\\u006d|\\u0064\\u006f\\u0078\\u0078|\\u0064\\u006f\\u0078\\u0078\\u0069\\u006e\\u0067|\\u0073\\u0077\\u0061\\u0074\\u0074\\u0069\\u006e\\u0067)\\b",
      "reason": "severe abuse, violence, self-harm and illegal content are banned",
      "level": "error"
    },
    {
      "pattern": "!\\[[^\\]]*\\]\\(https?://|data:(image|video|audio)/|\\b(imgur\\.com|i\\.imgur\\.com|tenor\\.com|media\\.tenor\\.com|giphy\\.com|media\\.giphy\\.com|catbox\\.moe|files\\.catbox\\.moe|ibb\\.co|i\\.ibb\\.co|postimages\\.org|prnt\\.sc|gyazo\\.com|puu\\.sh|streamable\\.com|imgflip\\.com|9gag\\.com|knowyourmeme\\.com|cdn\\.discordapp\\.com|media\\.discordapp\\.net)\\b|https?://[^\\s\"'<>)]+\\.(gif|webp|apng|mp4|webm|mov|avi|mkv)(\\b|[?#])",
      "reason": "external image, GIF and video embeds or unmoderated media hosts are banned",
      "level": "error",
      "binary": false
    }
  ]
}
""";

    private const string AllowedHostsJson = """
{
  "hosts": [
    "games.roblox.com",
    "thumbnails.roblox.com",
    "users.roblox.com",
    "apis.roblox.com",
    "api.github.com",
    "raw.githubusercontent.com"
  ]
}
""";

    private const string TagsJson = """
{
  "tags": [
    "appearance",
    "theme",
    "sidebar",
    "pages",
    "tools",
    "utility",
    "fastflags",
    "settings",
    "roblox",
    "games",
    "discord",
    "social",
    "stats",
    "performance",
    "automation",
    "accessibility",
    "fun",
    "developer",
    "example"
  ]
}
""";

    private static int MaxModBytes => Limit("maxModBytes", 1048576);

    private static int MaxFiles => Limit("maxFiles", 64);

    private static int MaxFileBytes => Limit("maxFileBytes", 262144);

    private static int MaxIconBytes => Limit("maxIconBytes", 262144);

    private static int MaxPathDepth => Limit("maxPathDepth", 4);

    private static int MaxLineLength => Limit("maxLineLength", 400);

    private static int MaxDescription => Limit("maxDescription", 300);

    private static int MaxEncodedRun => Limit("maxEncodedRun", 200);
}
