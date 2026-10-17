# NetSpeed v3
Live connection quality and speed in the Windows 10/11 tray.

- Live: burst of 4 network pings (ICMP) to 1.1.1.1 every 10 s, averaged over 30 s -> ping, jitter, offline. Icon colour:
  Colour is judged against this connection's own normal ping (learned automatically): green = within 30 ms of normal and jitter < 10 ms; orange = 30-60 ms above normal or jitter 10-30 ms; red = more than that, or offline.
- Speed test against Cloudflare's edge (speed.cloudflare.com) only when you click "Test speed now" (default), or automatically every 1 h, 30 min or 15 min if you choose.
  Test size adapts to the connection; timing starts when data arrives, so ping does not lower the result.
- Menu shows the data NetSpeed used this month.
- Icon: top = last measured download, bottom = live ping (ms). Hover for details.
- Diagnostic log: %LOCALAPPDATA%\NetSpeed
etspeed_log.csv (one line per ping burst and speed test; menu > Logs: Open log file, Open log folder, Copy logs to Desktop).
