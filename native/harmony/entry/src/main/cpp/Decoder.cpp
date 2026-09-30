#include <napi/native_api.h>
#include <multimedia/player_framework/native_avcodec_videodecoder.h>
#include <multimedia/player_framework/native_avcapability.h>
#include <multimedia/player_framework/native_avcodec_base.h>
#include <multimedia/player_framework/native_avbuffer.h>
#include <multimedia/player_framework/native_avbuffer_info.h>
#include <multimedia/player_framework/native_avformat.h>
#include <native_window/external_window.h>
#include <atomic>
#include <cmath>
#include <condition_variable>
#include <cstring>
#include <deque>
#include <memory>
#include <mutex>
#include <stdexcept>
#include <string>
#include <thread>
#include <unordered_set>
#include <vector>

namespace {
constexpr size_t MAX_BYTES = 16 * 1024 * 1024;
struct Buffer { uint32_t index; OH_AVBuffer* buffer; };
struct AccessUnit { std::vector<uint8_t> bytes; int64_t pts; uint32_t flags; };

// Callback threads only enqueue handles; all buffer calls run on one worker.
// Stop joins that worker before destroying codec handles or the native window.
class Decoder {
public:
    ~Decoder() { Stop(); }
    void Start(uint64_t surface, int width, int height, std::vector<uint8_t> config) {
        width_ = width; height_ = height;
        auto cap = OH_AVCodec_GetCapabilityByCategory(OH_AVCODEC_MIMETYPE_VIDEO_AVC, false, HARDWARE);
        if (!cap || !OH_AVCapability_IsHardware(cap)) throw std::runtime_error("设备没有可用的 H.264 硬件解码器");
        const char* name = OH_AVCapability_GetName(cap);
        if (!name) throw std::runtime_error("硬件解码器名称不可用");
        name_ = name;
        codec_ = OH_VideoDecoder_CreateByName(name);
        if (!codec_) throw std::runtime_error("无法创建 H.264 硬件解码器");
        if (OH_NativeWindow_CreateNativeWindowFromSurfaceId(surface, &window_) != 0 || !window_)
            throw std::runtime_error("原生 Surface 已失效");
        OH_AVCodecCallback callbacks{OnError, OnStreamChanged, OnInput, OnOutput};
        Check(OH_VideoDecoder_RegisterCallback(codec_, callbacks, this), "RegisterCallback");
        auto format = OH_AVFormat_Create();
        if (!format) throw std::runtime_error("无法创建视频格式");
        bool valid = OH_AVFormat_SetIntValue(format, OH_MD_KEY_WIDTH, width) &&
            OH_AVFormat_SetIntValue(format, OH_MD_KEY_HEIGHT, height);
        auto configured = valid ? OH_VideoDecoder_Configure(codec_, format) : AV_ERR_INVALID_VAL;
        OH_AVFormat_Destroy(format);
        Check(configured, "Configure");
        Check(OH_VideoDecoder_SetSurface(codec_, window_), "SetSurface");
        Check(OH_VideoDecoder_Prepare(codec_), "Prepare");
        queuedBytes_ = config.size();
        units_.push_back({std::move(config), -1, AVCODEC_BUFFER_FLAGS_CODEC_DATA});
        running_ = true;
        worker_ = std::thread([this] { Work(); });
        Check(OH_VideoDecoder_Start(codec_), "Start");
        started_ = true;
    }
    bool Push(const void* bytes, size_t length, int64_t pts) {
        std::lock_guard lock(mutex_);
        if (!running_ || length == 0 || length > 8 * 1024 * 1024 ||
            units_.size() >= 12 || queuedBytes_ + length > MAX_BYTES) return false;
        const auto* begin = static_cast<const uint8_t*>(bytes);
        units_.push_back({std::vector<uint8_t>(begin, begin + length), pts, AVCODEC_BUFFER_FLAGS_NONE});
        queuedBytes_ += length;
        wake_.notify_one();
        return true;
    }
    void Stop() {
        running_ = false;
        wake_.notify_all();
        if (worker_.joinable()) worker_.join();
        if (codec_) {
            if (started_) OH_VideoDecoder_Stop(codec_);
            OH_VideoDecoder_Destroy(codec_);
            codec_ = nullptr;
        }
        if (window_) { OH_NativeWindow_DestroyNativeWindow(window_); window_ = nullptr; }
    }
    napi_value Stats(napi_env env) {
        std::lock_guard lock(mutex_);
        napi_value result;
        napi_create_object(env, &result);
        Number(env, result, "submittedFrames", submitted_);
        Number(env, result, "lastPtsUs", lastPts_);
        Number(env, result, "width", width_);
        Number(env, result, "height", height_);
        Text(env, result, "error", error_);
        Text(env, result, "decoder", name_);
        return result;
    }
private:
    OH_AVCodec* codec_ = nullptr;
    OHNativeWindow* window_ = nullptr;
    bool started_ = false;
    std::atomic<bool> running_{false};
    std::mutex mutex_;
    std::condition_variable wake_;
    std::thread worker_;
    std::deque<Buffer> inputs_, outputs_;
    std::deque<AccessUnit> units_;
    std::unordered_set<int64_t> pendingPts_;
    size_t queuedBytes_ = 0;
    int width_ = 0, height_ = 0;
    uint64_t submitted_ = 0;
    int64_t lastPts_ = -1;
    std::string error_, name_;
    static void Check(OH_AVErrCode code, const char* operation) {
        if (code != AV_ERR_OK) throw std::runtime_error(std::string(operation) + " 失败，错误码 " + std::to_string(code));
    }
    void Fail(std::string message) {
        { std::lock_guard lock(mutex_); if (error_.empty()) error_ = std::move(message); }
        running_ = false; wake_.notify_all();
    }
    static void OnError(OH_AVCodec*, int32_t error, void* data) {
        static_cast<Decoder*>(data)->Fail("AVCodec 错误 " + std::to_string(error));
    }
    static void OnStreamChanged(OH_AVCodec*, OH_AVFormat* format, void* data) {
        auto self = static_cast<Decoder*>(data);
        int32_t width = 0, height = 0;
        if (format && OH_AVFormat_GetIntValue(format, OH_MD_KEY_WIDTH, &width) &&
            OH_AVFormat_GetIntValue(format, OH_MD_KEY_HEIGHT, &height) &&
            (width != self->width_ || height != self->height_)) {
            self->Fail("硬件解码输出尺寸与当前会话配置不一致");
        }
    }
    static void OnInput(OH_AVCodec*, uint32_t index, OH_AVBuffer* buffer, void* data) {
        auto self = static_cast<Decoder*>(data);
        std::lock_guard lock(self->mutex_);
        if (self->running_) { self->inputs_.push_back({index, buffer}); self->wake_.notify_one(); }
    }
    static void OnOutput(OH_AVCodec*, uint32_t index, OH_AVBuffer* buffer, void* data) {
        auto self = static_cast<Decoder*>(data);
        std::lock_guard lock(self->mutex_);
        if (self->running_) { self->outputs_.push_back({index, buffer}); self->wake_.notify_one(); }
    }
    void Work() noexcept {
        try {
            while (running_) {
                Buffer slot{};
                AccessUnit unit{};
                bool output = false;
                {
                    std::unique_lock lock(mutex_);
                    wake_.wait(lock, [this] { return !running_ || !outputs_.empty() || (!inputs_.empty() && !units_.empty()); });
                    if (!running_) break;
                    output = !outputs_.empty();
                    if (output) { slot = outputs_.front(); outputs_.pop_front(); }
                    else {
                        slot = inputs_.front(); inputs_.pop_front();
                        unit = std::move(units_.front()); units_.pop_front(); queuedBytes_ -= unit.bytes.size();
                    }
                }
                if (output) {
                    OH_AVCodecBufferAttr attr{};
                    Check(OH_AVBuffer_GetBufferAttr(slot.buffer, &attr), "GetBufferAttr");
                    // Only a PTS that came from a successfully queued access unit
                    // is eligible. Codec-data/EOS output is not a decoded frame.
                    if (pendingPts_.erase(attr.pts) != 0 && !(attr.flags & AVCODEC_BUFFER_FLAGS_EOS)) {
                        Check(OH_VideoDecoder_RenderOutputBuffer(codec_, slot.index), "RenderOutputBuffer");
                        std::lock_guard lock(mutex_);
                        ++submitted_; lastPts_ = attr.pts;
                        // This means surface submission, NOT compositor presentation.
                    } else Check(OH_VideoDecoder_FreeOutputBuffer(codec_, slot.index), "FreeOutputBuffer");
                } else {
                    auto address = OH_AVBuffer_GetAddr(slot.buffer);
                    auto capacity = OH_AVBuffer_GetCapacity(slot.buffer);
                    if (!address || capacity < 0 || unit.bytes.size() > static_cast<size_t>(capacity))
                        throw std::runtime_error("视频访问单元超过硬件输入缓冲区");
                    std::memcpy(address, unit.bytes.data(), unit.bytes.size());
                    OH_AVCodecBufferAttr attr{};
                    attr.pts = unit.pts; attr.size = static_cast<int32_t>(unit.bytes.size()); attr.offset = 0; attr.flags = unit.flags;
                    Check(OH_AVBuffer_SetBufferAttr(slot.buffer, &attr), "SetBufferAttr");
                    Check(OH_VideoDecoder_PushInputBuffer(codec_, slot.index), "PushInputBuffer");
                    if (unit.pts >= 0) pendingPts_.insert(unit.pts);
                    if (pendingPts_.size() > 256) throw std::runtime_error("硬件解码器没有及时返回输出");
                }
            }
        } catch (const std::exception& error) { Fail(error.what()); }
    }
    static void Number(napi_env env, napi_value object, const char* key, double value) {
        napi_value item; napi_create_double(env, value, &item); napi_set_named_property(env, object, key, item);
    }
    static void Text(napi_env env, napi_value object, const char* key, const std::string& value) {
        napi_value item; napi_create_string_utf8(env, value.c_str(), value.size(), &item); napi_set_named_property(env, object, key, item);
    }
};
std::mutex apiMutex;
std::unique_ptr<Decoder> decoder;
napi_value Undefined(napi_env env) { napi_value result; napi_get_undefined(env, &result); return result; }
void Require(napi_status status) { if (status != napi_ok) throw std::runtime_error("无效的原生解码参数"); }
napi_value Start(napi_env env, napi_callback_info info) {
    std::lock_guard lock(apiMutex);
    try {
        napi_value args[4]; size_t count = 4; Require(napi_get_cb_info(env, info, &count, args, nullptr, nullptr));
        if (count != 4) throw std::runtime_error("Start 参数不足");
        char surface[32]{}; size_t length = 0;
        Require(napi_get_value_string_utf8(env, args[0], surface, sizeof(surface), &length));
        if (!length || length >= sizeof(surface) - 1 || std::strspn(surface, "0123456789") != length)
            throw std::runtime_error("无效的 Surface 标识");
        int32_t width, height; Require(napi_get_value_int32(env, args[1], &width)); Require(napi_get_value_int32(env, args[2], &height));
        if (width < 240 || height < 240 || width > 8192 || height > 8192 || (width & 1) || (height & 1) ||
            static_cast<int64_t>(width) * height > 16000000)
            throw std::runtime_error("无效的视频尺寸");
        void* bytes; size_t size; Require(napi_get_arraybuffer_info(env, args[3], &bytes, &size));
        if (!size || size > 131072) throw std::runtime_error("无效的 H.264 配置");
        decoder.reset();
        auto next = std::make_unique<Decoder>();
        auto begin = static_cast<const uint8_t*>(bytes);
        next->Start(std::stoull(surface), width, height, std::vector<uint8_t>(begin, begin + size));
        decoder = std::move(next);
    } catch (const std::exception& error) { napi_throw_error(env, nullptr, error.what()); }
    return Undefined(env);
}
napi_value Push(napi_env env, napi_callback_info info) {
    std::lock_guard lock(apiMutex);
    bool accepted = false;
    try {
        napi_value args[2]; size_t count = 2; Require(napi_get_cb_info(env, info, &count, args, nullptr, nullptr));
        if (count != 2 || !decoder) throw std::runtime_error("解码器尚未启动");
        void* bytes; size_t size; double pts;
        Require(napi_get_arraybuffer_info(env, args[0], &bytes, &size)); Require(napi_get_value_double(env, args[1], &pts));
        if (!std::isfinite(pts) || pts < 0 || pts > 9007199254740991.0 || std::floor(pts) != pts)
            throw std::runtime_error("无效的视频 PTS");
        accepted = decoder->Push(bytes, size, static_cast<int64_t>(pts));
    } catch (const std::exception& error) { napi_throw_error(env, nullptr, error.what()); }
    napi_value result; napi_get_boolean(env, accepted, &result); return result;
}
napi_value Stats(napi_env env, napi_callback_info) {
    std::lock_guard lock(apiMutex);
    if (!decoder) { napi_throw_error(env, nullptr, "解码器尚未启动"); return Undefined(env); }
    return decoder->Stats(env);
}
napi_value Stop(napi_env env, napi_callback_info) { std::lock_guard lock(apiMutex); decoder.reset(); return Undefined(env); }
napi_value Init(napi_env env, napi_value exports) {
    napi_property_descriptor properties[] = {
        {"start", nullptr, Start, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"push", nullptr, Push, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"stats", nullptr, Stats, nullptr, nullptr, nullptr, napi_default, nullptr},
        {"stop", nullptr, Stop, nullptr, nullptr, nullptr, napi_default, nullptr}
    };
    napi_define_properties(env, exports, sizeof(properties) / sizeof(properties[0]), properties);
    napi_add_env_cleanup_hook(env, [](void*) { std::lock_guard lock(apiMutex); decoder.reset(); }, nullptr);
    return exports;
}
napi_module module{1, 0, nullptr, Init, "tablink_codec", nullptr, {nullptr}};
__attribute__((constructor)) void RegisterModule() { napi_module_register(&module); }
} // namespace
