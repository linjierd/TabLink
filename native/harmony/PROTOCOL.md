# Harmony 原生协议

传输为单个 TLS TCP 连接。URI 中的 token/cert 必须来自用户信任的电脑 TabLink 界面。客户端不跟随 URL 重定向，不发现其他设备；仅接受显式 IPv4 和数字端口，拒绝重复参数。

原生端口白名单为 `27184, 27186, 27187, 27188, 27189, 27190, 27191, 27192`；`27185` 是浏览器专用端口，不接受。URI token 必须是 64 个小写十六进制字符；证书指纹允许大小写，比较时规范化。拒绝 percent 编码、额外路径、fragment、未知/重复参数和非规范 IPv4。

每个包：`type: uint8`，`length: uint32 big-endian`，然后 `length` 字节。长度必须是 1..8,388,608。文本使用 UTF-8 JSON。

| 方向 | type | 内容 |
| --- | --- | --- |
| 客户端→电脑 | `0x10` | `{"protocol":1,"token":"64个十六进制字符","features":["render-submitted-v1"]}`；只能在证书 DER SHA256 校验后发送；客户端必须等电脑在 `0x02` 中回显同一协议与能力后，才可发送 `0x14` |
| 客户端→电脑 | `0x13` | `width,height,rotation,activeModeId,refreshRate,nativeWidth,nativeHeight,supportedModes`，另带 `clientPlatform:"harmony",progressEvidence:"render-submitted"` |
| 电脑→客户端 | `0x02` / `0x03` | `{"message":"..."}` 状态 / 错误 |
| 电脑→客户端 | `0x20` | `{"codec":"video/avc","width":W,"height":H,"fps":F,"csd0":"base64 SPS","csd1":"base64 PPS"}` |
| 电脑→客户端 | `0x21` | `ptsUs: int64 big-endian` + 完整 H.264 Annex-B access unit；PTS 非负且递增 |
| 客户端→电脑 | `0x14` | 下方的解码提交进度；必须与电脑本次连接实际发送的 PTS、尺寸、帧数上界核对 |

```json
{
  "width": 1920,
  "height": 1080,
  "ptsUs": 123456,
  "frames": 50,
  "fps": 59.8,
  "decoder": "Harmony AVCodec",
  "evidence": "render-submitted"
}
```

`frames` 是本 TLS 连接内递增的已提交输出总数，重新配置解码器时继续累计。`ptsUs` 必须来自成功 `PushInputBuffer` 对应的实际解码输出，并且 `RenderOutputBuffer` 返回成功。只有帧计数进展时才发送 0x14。统计名称不能写成 presented / 显示帧率；不发送 0x12。主机应使用单独的 submitted 进度作为此类客户端存活信号，不能增加实际呈现计数。

重新配置后，必须等待新解码器 `submittedFrames > 0` 且 `lastPtsUs >= 0` 才能上报；旧配置累积基数本身不能产生有效进度。服务器 `capturePaused:true` 期间暂停普通的无输出超时和提交上报，等待恢复，不发保活假帧。

旋转时客户端关闭旧连接/解码器，重新读取全屏尺寸，用同一个设备会话的现有配对信息建立新连接。电脑端负责归还/重新分配同一设备的目标，不影响其他会话。

当前接收端没有输入回传，因此不会发送 0x11。电脑仍可用本机鼠标和键盘操作扩展屏。
