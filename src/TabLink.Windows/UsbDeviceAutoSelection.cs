namespace TabLink.Windows;

internal static class UsbDeviceAutoSelection
{
    internal static int FindPreferredIndex(IReadOnlyList<(string Serial,bool Allowed)> devices,string? previousSerial)
    {
        ArgumentNullException.ThrowIfNull(devices);
        if(!string.IsNullOrWhiteSpace(previousSerial))
        {
            for(var index=0;index<devices.Count;index++)
                if(devices[index].Allowed&&string.Equals(devices[index].Serial,previousSerial,StringComparison.Ordinal))
                    return index;
        }

        var onlyAllowed=-1;
        for(var index=0;index<devices.Count;index++)
        {
            if(!devices[index].Allowed)continue;
            if(onlyAllowed>=0)return -1;
            onlyAllowed=index;
        }
        return onlyAllowed;
    }
}
