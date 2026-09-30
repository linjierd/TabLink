#define _WIN32_WINNT 0x0A00
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11_1.h>
#include <dxgi1_3.h>
#include <algorithm>
#include <cstdio>
#include <cstdlib>
#include <cwchar>
#include <stdexcept>
#include <string>
#include <vector>

template<class T> struct Com {
    T *p = nullptr;
    ~Com() { if (p) p->Release(); }
    T **out() { return &p; }
    T *operator->() const { return p; }
    Com() = default;
    Com(const Com &) = delete;
    Com &operator=(const Com &) = delete;
};

static void require_hr(HRESULT hr, const char *operation) {
    if (FAILED(hr)) {
        char message[200];
        std::snprintf(message, sizeof message, "%s failed: HRESULT 0x%08lx", operation, (unsigned long)hr);
        throw std::runtime_error(message);
    }
}

struct Target {
    std::wstring name;
    RECT bounds{};
    LUID luid{};
    int seconds = 12;
    bool aborted = false;
    HWND window = nullptr;
};

static bool current_bounds(const Target &t) {
    DEVMODEW mode{};
    mode.dmSize = sizeof mode;
    if (!EnumDisplaySettingsExW(t.name.c_str(), ENUM_CURRENT_SETTINGS, &mode, 0)) return false;
    if ((LONG)mode.dmPelsWidth != t.bounds.right - t.bounds.left ||
        (LONG)mode.dmPelsHeight != t.bounds.bottom - t.bounds.top ||
        mode.dmPosition.x != t.bounds.left || mode.dmPosition.y != t.bounds.top ||
        mode.dmDisplayFrequency != 90) return false;
    for (DWORD i = 0;; i++) {
        DISPLAY_DEVICEW device{}; device.cb = sizeof device;
        if (!EnumDisplayDevicesW(nullptr, i, &device, 0)) return false;
        if (!_wcsicmp(device.DeviceName, t.name.c_str()))
            return (device.StateFlags & DISPLAY_DEVICE_ATTACHED_TO_DESKTOP) &&
                   !(device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE);
    }
}

static LRESULT CALLBACK window_proc(HWND window, UINT message, WPARAM wparam, LPARAM lparam) {
    Target *t = reinterpret_cast<Target *>(GetWindowLongPtrW(window, GWLP_USERDATA));
    if (message == WM_NCCREATE) {
        auto create = reinterpret_cast<CREATESTRUCTW *>(lparam);
        t = reinterpret_cast<Target *>(create->lpCreateParams);
        SetWindowLongPtrW(window, GWLP_USERDATA, reinterpret_cast<LONG_PTR>(t));
    }
    if (t) {
        bool changed = message == WM_DISPLAYCHANGE || message == WM_DPICHANGED;
        if (message == WM_WINDOWPOSCHANGING) {
            auto position = reinterpret_cast<WINDOWPOS *>(lparam);
            if (!(position->flags & SWP_NOMOVE) &&
                (position->x != t->bounds.left || position->y != t->bounds.top)) changed = true;
            if (!(position->flags & SWP_NOSIZE) &&
                (position->cx != t->bounds.right - t->bounds.left ||
                 position->cy != t->bounds.bottom - t->bounds.top)) changed = true;
        }
        if (changed) {
            t->aborted = true;
            ShowWindow(window, SW_HIDE);
            PostMessageW(window, WM_CLOSE, 0, 0);
        }
    }
    if (message == WM_MOUSEACTIVATE) return MA_NOACTIVATE;
    if (message == WM_CLOSE) { DestroyWindow(window); return 0; }
    if (message == WM_DESTROY) { if (t) t->window = nullptr; PostQuitMessage(0); return 0; }
    return DefWindowProcW(window, message, wparam, lparam);
}

static double now_seconds() {
    LARGE_INTEGER counter, frequency;
    QueryPerformanceCounter(&counter);
    QueryPerformanceFrequency(&frequency);
    return double(counter.QuadPart) / frequency.QuadPart;
}

int wmain(int argc, wchar_t **argv) {
    Target target;
    HANDLE latency = nullptr;
    try {
        if (argc != 9) throw std::runtime_error("Expected device x y width height seconds luid-low luid-high");
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);
        target.name = argv[1];
        target.bounds.left = std::wcstol(argv[2], nullptr, 10);
        target.bounds.top = std::wcstol(argv[3], nullptr, 10);
        target.bounds.right = target.bounds.left + std::wcstol(argv[4], nullptr, 10);
        target.bounds.bottom = target.bounds.top + std::wcstol(argv[5], nullptr, 10);
        target.seconds = std::clamp(int(std::wcstol(argv[6], nullptr, 10)), 2, 150);
        target.luid.LowPart = std::wcstoul(argv[7], nullptr, 10);
        target.luid.HighPart = std::wcstol(argv[8], nullptr, 10);
        if (!current_bounds(target)) throw std::runtime_error("Target is not the same active non-primary 90 Hz display");

        Com<IDXGIFactory2> factory;
        require_hr(CreateDXGIFactory1(__uuidof(IDXGIFactory2), reinterpret_cast<void **>(factory.out())), "CreateDXGIFactory1");
        Com<IDXGIAdapter1> selected_adapter;
        Com<IDXGIOutput> selected_output;
        unsigned matches = 0;
        for (UINT a = 0;; a++) {
            Com<IDXGIAdapter1> adapter;
            HRESULT ar = factory->EnumAdapters1(a, adapter.out());
            if (ar == DXGI_ERROR_NOT_FOUND) break;
            require_hr(ar, "EnumAdapters1");
            DXGI_ADAPTER_DESC1 desc{};
            require_hr(adapter->GetDesc1(&desc), "GetDesc1");
            for (UINT o = 0;; o++) {
                Com<IDXGIOutput> output;
                HRESULT rr = adapter->EnumOutputs(o, output.out());
                if (rr == DXGI_ERROR_NOT_FOUND) break;
                require_hr(rr, "EnumOutputs");
                DXGI_OUTPUT_DESC out{};
                require_hr(output->GetDesc(&out), "Output GetDesc");
                if (!_wcsicmp(out.DeviceName, target.name.c_str()) && out.AttachedToDesktop &&
                    EqualRect(&out.DesktopCoordinates, &target.bounds) &&
                    desc.AdapterLuid.LowPart == target.luid.LowPart && desc.AdapterLuid.HighPart == target.luid.HighPart) {
                    matches++;
                    if (matches == 1) {
                        selected_adapter.p = adapter.p; adapter.p = nullptr;
                        selected_output.p = output.p; output.p = nullptr;
                        // The selected adapter remains valid for this loop.
                        selected_adapter->AddRef(); adapter.p = selected_adapter.p;
                    }
                }
            }
        }
        if (matches != 1) throw std::runtime_error("DXGI output identity/bounds are missing or ambiguous");

        Com<ID3D11Device> device;
        Com<ID3D11DeviceContext> context;
        const D3D_FEATURE_LEVEL levels[] = { D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0 };
        D3D_FEATURE_LEVEL level;
        require_hr(D3D11CreateDevice(selected_adapter.p, D3D_DRIVER_TYPE_UNKNOWN, nullptr,
                    D3D11_CREATE_DEVICE_BGRA_SUPPORT, levels, 2, D3D11_SDK_VERSION,
                    device.out(), &level, context.out()), "D3D11CreateDevice");
        Com<ID3D11DeviceContext1> context1;
        require_hr(context->QueryInterface(__uuidof(ID3D11DeviceContext1), reinterpret_cast<void **>(context1.out())), "ID3D11DeviceContext1");
        WNDCLASSW cls{};
        cls.lpfnWndProc = window_proc; cls.hInstance = GetModuleHandleW(nullptr); cls.lpszClassName = L"TabLinkD3DMotionProbe";
        if (!RegisterClassW(&cls)) throw std::runtime_error("RegisterClass failed");
        if (!current_bounds(target)) throw std::runtime_error("Display changed before window creation");
        target.window = CreateWindowExW(WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE, cls.lpszClassName,
            L"TabLink native D3D11 motion probe", WS_POPUP, target.bounds.left, target.bounds.top,
            target.bounds.right-target.bounds.left, target.bounds.bottom-target.bounds.top,
            nullptr, nullptr, cls.hInstance, &target);
        if (!target.window || target.aborted) throw std::runtime_error("Window creation/position validation failed");

        DXGI_SWAP_CHAIN_DESC1 desc{};
        desc.Width = target.bounds.right-target.bounds.left; desc.Height = target.bounds.bottom-target.bounds.top;
        desc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; desc.SampleDesc.Count = 1;
        desc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT; desc.BufferCount = 2;
        desc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_DISCARD;
        desc.Flags = DXGI_SWAP_CHAIN_FLAG_FRAME_LATENCY_WAITABLE_OBJECT;
        Com<IDXGISwapChain1> swap;
        require_hr(factory->CreateSwapChainForHwnd(device.p, target.window, &desc, nullptr, selected_output.p, swap.out()), "CreateSwapChainForHwnd");
        factory->MakeWindowAssociation(target.window, DXGI_MWA_NO_ALT_ENTER);
        Com<IDXGISwapChain2> swap2;
        require_hr(swap->QueryInterface(__uuidof(IDXGISwapChain2), reinterpret_cast<void **>(swap2.out())), "IDXGISwapChain2");
        require_hr(swap2->SetMaximumFrameLatency(1), "SetMaximumFrameLatency");
        latency = swap2->GetFrameLatencyWaitableObject();
        if (!latency) throw std::runtime_error("No frame-latency wait handle");
        Com<ID3D11Texture2D> texture;
        require_hr(swap->GetBuffer(0, __uuidof(ID3D11Texture2D), reinterpret_cast<void **>(texture.out())), "GetBuffer");
        Com<ID3D11RenderTargetView> view;
        require_hr(device->CreateRenderTargetView(texture.p, nullptr, view.out()), "CreateRenderTargetView");
        SetWindowPos(target.window, HWND_TOPMOST, target.bounds.left, target.bounds.top, desc.Width, desc.Height,
                     SWP_NOACTIVATE | SWP_SHOWWINDOW);
        const float background[4] = { .047f, .090f, .157f, 1.f };
        const float black[4] = { 0,0,0,1 }, white[4] = { 1,1,1,1 }, green[4] = { .2f,.84f,.65f,1 };
        std::vector<double> presented;
        const double start = now_seconds();
        double checked = start;
        while (now_seconds()-start < target.seconds) {
            MSG msg;
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                if (msg.message == WM_QUIT) target.aborted = true;
                TranslateMessage(&msg); DispatchMessageW(&msg);
            }
            if (target.aborted || !target.window) throw std::runtime_error("Display/window topology changed; probe stopped");
            if (now_seconds()-checked > .1) {
                if (!current_bounds(target)) throw std::runtime_error("Native target identity/bounds/mode changed");
                checked = now_seconds();
            }
            if (WaitForSingleObject(latency, 1000) != WAIT_OBJECT_0) throw std::runtime_error("Present wait timed out");
            context->ClearRenderTargetView(view.p, background);
            unsigned frame = unsigned(presented.size());
            for (unsigned bit=0; bit<16; bit++) {
                D3D11_RECT cell = { LONG(48+bit*28),220,LONG(68+bit*28),240 };
                context1->ClearView(view.p, (frame & (1u<<bit)) ? white : black, &cell, 1);
            }
            LONG x = 48 + LONG((now_seconds()-start)*270) % std::max(1, int(desc.Width)-180);
            D3D11_RECT bar = {x,320,x+84,LONG(desc.Height)-110};
            context1->ClearView(view.p, green, &bar, 1);
            require_hr(swap->Present(1,0), "Present");
            presented.push_back(now_seconds()-start);
        }
        ShowWindow(target.window, SW_HIDE);
        DestroyWindow(target.window);
        CloseHandle(latency); latency=nullptr;
        std::vector<double> gaps;
        for(size_t i=1;i<presented.size();i++) gaps.push_back((presented[i]-presented[i-1])*1000);
        std::sort(gaps.begin(),gaps.end());
        double fps=presented.size()>1?(presented.size()-1)/(presented.back()-presented.front()):0;
        std::printf("{\"Success\":true,\"SourceKind\":\"d3d11-flip-discard\",\"VerifiedDisplayModeHz\":90,\"PresentSyncInterval\":1,\"RequestedSeconds\":%d,\"PresentCount\":%zu,\"PresentFps\":%.6f,\"MedianPresentGapMs\":%.6f,\"P90PresentGapMs\":%.6f,\"PresentTimes\":[",
                    target.seconds,presented.size(),fps,gaps.empty()?0:gaps[gaps.size()/2],gaps.empty()?0:gaps[size_t(gaps.size()*.9)]);
        for(size_t i=0;i<presented.size();i++) std::printf("%s%.6f",i?",":"",presented[i]);
        std::puts("]}");
        return presented.size()>1?0:1;
    } catch (const std::exception &error) {
        if(target.window) {ShowWindow(target.window,SW_HIDE);DestroyWindow(target.window);}
        if(latency) CloseHandle(latency);
        std::fprintf(stderr,"%s\n",error.what());
        return 1;
    }
}
