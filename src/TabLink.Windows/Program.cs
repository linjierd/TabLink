namespace TabLink.Windows;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        UpdateStartupHealthSignal? updateHealth = null;
        if (args.Length == 3 && args[0] == "--post-update-health")
        {
            updateHealth = UpdateStartupHealthSignal.Parse(args[1], args[2]);
            args = [];
        }
        if(args.Length==2&&args[0]=="--probe-displays")
        {File.WriteAllText(args[1],System.Text.Json.JsonSerializer.Serialize(VirtualDisplayManager.GetTargets(),new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));return;}
        if(args.Length==2&&args[0]=="--render-ui")
        {MainForm.RenderUi(args[1]);return;}
        if(args.Length==3&&args[0]=="--watch-display")
        {
            Environment.ExitCode=SessionGuard.WatchAsync(args[1],args[2],
                SingleDisplayDriverLifecycle.Shared.RemoveAfterOwnerExitAsync).GetAwaiter().GetResult();return;
        }
        if(args.Length==4&&args[0]=="--watch-driver-owner")
        {
            try
            {
                Environment.ExitCode=DriverOwnerWatchdog.RunChildAsync(args,
                    SingleDisplayDriverLifecycle.Shared.RemoveAfterOwnerExitAsync).GetAwaiter().GetResult();
            }
            catch(Exception ex)
            {
                Diagnostics.Save("driver-owner-watchdog-error.json",()=>new{timestamp=DateTimeOffset.Now,error=ex.ToString()});
                Environment.ExitCode=1;
            }
            return;
        }
        if(args.Length>0 && args[0] is "--probe-usb" or "--usb-smoke")
        {
            try { Environment.ExitCode=args[0]=="--probe-usb"?Diagnostics.ProbeAsync().GetAwaiter().GetResult():Diagnostics.SmokeAsync(args.Length>1?args[1]:throw new ArgumentException("Missing serial"),args.Length>2?int.Parse(args[2]):20).GetAwaiter().GetResult(); }
            catch(Exception ex){Diagnostics.Save("diagnostic-error.json",()=>new{error=ex.ToString(),timestamp=DateTimeOffset.Now});Environment.ExitCode=1;}
            return;
        }
        if (args.Contains("--self-test"))
        {
            try { SelfTests.RunAsync().GetAwaiter().GetResult(); Environment.ExitCode = 0; }
            catch (Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "selftest-result.txt"), ex.ToString()); Environment.ExitCode = 1; }
            return;
        }
        if(args.Length==1&&args[0]=="--exit")
        {
            try{using var request=EventWaitHandle.OpenExisting(@"Local\TabLink.Windows.Exit");request.Set();}
            catch(WaitHandleCannotBeOpenedException){}
            return;
        }
        using var activation=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\TabLink.Windows.Show");
        using var exitRequest=new EventWaitHandle(false,EventResetMode.AutoReset,@"Local\TabLink.Windows.Exit");
        using var instance=new Mutex(true,@"Local\TabLink.Windows.UI",out var ownsInstance);
        if(!ownsInstance){activation.Set();return;}
        try
        {
            // Complete the independent owner-process handshake before a form,
            // listener, helper or display reservation can be created.
            DriverOwnerWatchdog.StartForCurrentProcess();
            var form = new MainForm(args.Length==2&&args[0]=="--connect"?args[1]:null,activation,
            args.Length==2&&args[0] is "--network" or "--diagnostic-network"?args[1]:null,
            args.Length==2&&args[0]=="--diagnostic-network",exitRequest);
            if(updateHealth is not null) form.Shown += (_,_) => updateHealth.MarkReady();
            Application.Run(form);
        }
        finally{instance.ReleaseMutex();}
    }
}
