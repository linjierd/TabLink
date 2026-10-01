using System.Text.Json;
using System.Text.Json.Serialization;

namespace TabLink.Core;

public sealed record TabletDisplayMode(int Width,int Height,double RefreshRate,int ModeId);

public sealed record TabletDisplayProfile(int Width,int Height,int Rotation,int ActiveModeId,double RefreshRate,
    int NativeWidth,int NativeHeight,IReadOnlyList<TabletDisplayMode> SupportedModes)
{
    [JsonIgnore] public int RequestedRefreshRate => (int)Math.Round(SupportedModes
        .Where(m=>m.Width==NativeWidth&&m.Height==NativeHeight)
        .Select(m=>m.RefreshRate).DefaultIfEmpty(RefreshRate).Max());

    public static TabletDisplayProfile Parse(string json)
    {
        if(json.Length>65536)throw new InvalidDataException("平板显示信息过大");
        var result=JsonSerializer.Deserialize<TabletDisplayProfile>(json,new JsonSerializerOptions{PropertyNameCaseInsensitive=true})
            ??throw new InvalidDataException("平板没有返回显示信息");
        result.Validate();return result;
    }
    public void Validate()
    {
        static bool Size(int n)=>n>=240&&n<=8192;
        static bool Hz(double n)=>double.IsFinite(n)&&n>=24&&n<=240;
        if(!Size(Width)||!Size(Height)||!Size(NativeWidth)||!Size(NativeHeight)||Rotation is <0 or >3||!Hz(RefreshRate)
            ||SupportedModes is null||SupportedModes.Count is <1 or >128
            ||SupportedModes.Any(m=>m is null||!Size(m.Width)||!Size(m.Height)||!Hz(m.RefreshRate))
            ||(long)Width*Height>16_000_000||(long)NativeWidth*NativeHeight>16_000_000
            ||SupportedModes.Any(m=>(long)m.Width*m.Height>16_000_000)
            ||!SupportedModes.Any(m=>m.Width==NativeWidth&&m.Height==NativeHeight)
            ||!((Width==NativeWidth&&Height==NativeHeight)||(Width==NativeHeight&&Height==NativeWidth)))
            throw new InvalidDataException("APK 返回的屏幕分辨率或刷新率无效，已停止配置副屏。");
        if((Width&1)!=0||(Height&1)!=0)throw new InvalidDataException("当前屏幕尺寸不支持 H.264 的偶数像素要求。");
    }
}
