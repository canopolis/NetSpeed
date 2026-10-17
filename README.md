# NetSpeed v3
Live connection quality and speed in the Windows 10/11 tray.

- Live: burst of 4 network pings (ICMP) to 1.1.1.1 every 10 s, averaged over 30 s -> ping, jitter, offline. Icon colour:
  green = ping < 50 ms and jitter < 5 ms; orange = ping 50-100 ms or jitter 5-10 ms; red = ping > 100 ms, jitter > 10 ms or offline.
- Speed test against Cloudflare's edge (speed.cloudflare.com) only when you click "Test speed now" (default), or automatically every 1 h, 30 min or 15 min if you choose.
  Test size adapts to the connection; timing starts when data arrives, so ping does not lower the result.
- Menu shows the data NetSpeed used this month.
- Icon: top = last measured download, bottom = live ping (ms). Hover for details.
