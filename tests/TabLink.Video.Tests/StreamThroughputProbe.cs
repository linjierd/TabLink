using System.Diagnostics;
using System.Text.Json;
using TabLink.Core;
using TabLink.Windows;

internal static class StreamThroughputProbe
{
    internal static async Task RunAsync(string path)
    {
        var display=VirtualDisplayManager.GetDisplays().Single(x=>x.IsTabLinkCompatible&&!x.IsPrimary);
        var lease=VirtualDisplayManager.CaptureLease(display);
        var fps=(int)lease.OriginalMode.Frequency;
        var profile=new TabletDisplayProfile(display.Bounds.Width,display.Bounds.Height,0,1,fps,
            display.Bounds.Width,display.Bounds.Height,[new(display.Bounds.Width,display.Bounds.Height,fps,1)]);
        var reports=new List<object>();
        foreach(var pipeline in new[]{false,true})
        {
            using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(18));
            var gaps=new List<double>();long last=0;int count=0;long bytes=0;
            void Observe(int length)
            {
                var now=Stopwatch.GetTimestamp();
                if(last!=0)gaps.Add(Stopwatch.GetElapsedTime(last,now).TotalMilliseconds);
                last=now;count++;bytes+=length;
            }
            if(pipeline)
            {
                await foreach(var packet in VideoPipeline.StreamAsync(display,profile,deadline.Token,null,lease))
                {if(!packet.IsFrame)continue;Observe(packet.Payload.Length);if(count>=fps*5)break;}
            }
            else
            {
                await using var encoder=new H264Encoder(display,display.Bounds.Width,display.Bounds.Height,fps,
                    VideoPipeline.FindFfmpeg(),capturePreference:H264CapturePreference.PreferDesktopDuplication,identity:lease);
                encoder.Start();
                await foreach(var unit in encoder.ReadAccessUnitsAsync(deadline.Token))
                {Observe(unit.Data.Length);if(count>=fps*5)break;}
            }
            var sorted=gaps.Order().ToArray();
            reports.Add(new{mode=pipeline?"pipeline-memory-sink":"encoder-memory-sink",count,bytes,
                fps=gaps.Count*1000.0/gaps.Sum(),maxGapMs=sorted[^1],p95Ms=sorted[(int)(sorted.Length*.95)],
                burstsUnder2ms=gaps.Count(x=>x<2),gapsOver25ms=gaps.Count(x=>x>25)});
        }
        _=VirtualDisplayManager.ResolveCurrent(lease);
        var json=JsonSerializer.Serialize(new{timestamp=DateTimeOffset.Now,display,requestedFps=fps,reports},new JsonSerializerOptions{WriteIndented=true});
        File.WriteAllText(path,json);Console.WriteLine(json);
    }
}
