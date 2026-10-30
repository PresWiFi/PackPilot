# Third-party notices

## SwitchHunt

PackPilot's `.intunewin` format handling, Windows Sandbox test flow and installer-engine signatures are adapted from SwitchHunt (https://github.com/deadarcher/SwitchHunt).

```
MIT License

Copyright (c) 2026 Vitko Software, LLC

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## IntuneWin32App (MSEndpointMgr)

The Microsoft Graph request sequence for uploading Win32 apps follows the IntuneWin32App PowerShell module (https://github.com/MSEndpointMgr/IntuneWin32App, MIT License). No code is copied from it. `Deploy-ToIntune.ps1` installs and uses the module.

## Microsoft Authentication Library (MSAL.NET)

`PackPilot.exe` includes Microsoft.Identity.Client (https://github.com/AzureAD/microsoft-authentication-library-for-dotnet), MIT License, Copyright (c) Microsoft Corporation.

## .NET

`PackPilot.exe` is a self-contained .NET 8 application and includes the .NET runtime (https://github.com/dotnet/runtime), MIT License, Copyright (c) .NET Foundation and Contributors.
