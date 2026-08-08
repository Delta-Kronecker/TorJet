# Tor Portable for Iran

یک پکیج کاملاً پرتابل تور برای ویندوز — همهی فایلها کنار هم در یک پوشه، فقط با مسیرهای نسبی (بدون `%APPDATA%`).

## استفاده

1. پوشهی `tor-win64-portable` را از آرتیفکت GitHub Actions دانلود و اکسترکت کنید.
2. `start.bat` را دوبار کلیک کنید (یا `scripts\launcher.ps1`).
3. تور تا ۱۰۰٪ بوتاسترپ میشود و پراکسی سیستم روی `127.0.0.1:8118` تنظیم میشود.
   - SOCKS5: `127.0.0.1:9050` — DNS: `127.0.0.1:53530` — Control: `127.0.0.1:9051`

## محتویات

```
tor-win64-portable\
  tor.exe + DLLها
  webtunnel.exe, obfs4proxy.exe, snowflake-client.exe  (برای پل)
  torrc                <- کانفیگ کامل (پیشفرض: اتصال مستقیم)
  geoip, geoip6
  start.bat
  data\                <- ساخته میشود (tor.log، bridges.txt)
  scripts\launcher.ps1
  scripts\fetch-bridges.ps1
```

## اگر اتصال مستقیم بلاک شد

اینترنت ایران گاهی اتصال مستقیم تور را مسدود میکند. در این حالت:

1. `scripts\fetch-bridges.ps1 -Transports obfs4 -PerTransport 20` را اجرا کنید تا پلهای تازه از گیتهاب بگیرد و به `torrc` اضافه کند.
2. دوباره `start.bat` را اجرا کنید.

### نکات

- هرگز `tor.exe` را بدون آرگیومنت اجرا نکنید؛ همیشه با `-f torrc` (لاانچر خودش این کار را میکند).
- رمز ControlPort: `newway-j7DJPvxLaS1H` (برای SIGNAL NEWNYM).
- `launcher.ps1 -NewCircuit` برای هویت جدید، `launcher.ps1 -Stop` برای توقف.

## ساخت از سورس (CI)

ورکفلو `.github/workflows/build.yml` از سورس رسمی tor 0.4.9.11 (تاربال
`dist.torproject.org`) با MSYS2 بیلد میگیرد و یک آرتیفکت واحد
`tor-win64-portable` آپلود میکند.

نکته: گزینههای `ConfluxEnabled` و `ExitNodes/StrictNodes` عمداً حذف شدهاند — تست
نشان داد بوتاسترپ را روی ۴۵–۵۰٪ متوقف میکنند.
