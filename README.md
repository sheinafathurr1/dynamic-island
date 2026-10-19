# Dynamic Island untuk Windows

Island / notch ala iPhone & MacBook di bagian atas layar Windows. Dibuat native (C# WPF, .NET 10)
supaya **ringan**: tidak ada Electron atau browser tersembunyi, tidak ada polling, dan tidak ada render
saat island diam.

```
   idle                 musik diputar                    di-hover / diklik
  ▄▄▄▄▄▄▄▄          ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄         ▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄▄
 (        )        ( ▣              ▂▅▃▇ )       ( ▣▣  Judul Lagu          ▂▅▃▇ )
  ▀▀▀▀▀▀▀▀          ▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀        (     Artis                     )
                                                (  0:42 ━━━━━━━──────── -2:31   )
                                                (        ⏮     ⏯     ⏭         )
                                                 ▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀▀
```

## Fitur

- **Now Playing**: Spotify, YouTube/Chrome/Edge, Media Player, dan semua aplikasi yang muncul di
  kontrol media Windows. Mode ringkas menampilkan cover album + equalizer yang warnanya diambil dari
  cover. Hover atau klik untuk membuka panel penuh: judul, artis, progress bar (bisa diklik untuk
  *seek*), serta tombol previous / play-pause / next.
- **Lagu baru**: notifikasi singkat berisi cover, judul, dan artis setiap kali lagu berganti.
- **Volume**: indikator volume & mute saat volume diubah (tombol keyboard, tray, atau aplikasi).
- **Baterai**: "Mengisi daya" saat charger dicolok, "Menggunakan baterai" saat dicabut, dan
  "Baterai lemah" ketika baterai turun ke 20% dan 10%.
- **Jam & tanggal**: hover island saat tidak ada media untuk melihat jam, tanggal, dan baterai.
- **Dua gaya**: *Island* (pil melayang) atau *Notch* (menempel di tepi atas dengan lekukan seperti
  MacBook).
- **Otomatis sembunyi** saat ada aplikasi fullscreen (game, video, presentasi).
- Animasi pegas (*spring*) dengan sedikit pantulan seperti aslinya, tajam di semua skala DPI.
- Tidak mencuri fokus saat diklik dan tidak muncul di taskbar maupun Alt+Tab.

Klik kanan island untuk pengaturan: gaya island/notch, tampilkan volume, tampilkan lagu baru,
jalankan saat Windows mulai, dan keluar.

## Kenapa ringan

| Bagian | Cara kerjanya |
| --- | --- |
| Animasi bentuk | Simulasi pegas yang hanya terhubung ke render loop **selama bergerak**, lalu lepas lagi |
| Equalizer | 30 fps, dan hanya saat musik diputar **dan** terlihat di layar |
| Media | Event dari `GlobalSystemMediaTransportControls`, tanpa polling |
| Volume | Callback Core Audio (`IAudioEndpointVolumeCallback`), tanpa polling |
| Baterai | Pesan `WM_POWERBROADCAST` dari Windows, tanpa polling |
| Fullscreen | Notifikasi AppBar `ABN_FULLSCREENAPP` dari shell, tanpa polling |
| Timer | Hanya jalan saat panel jam/media sedang terbuka (update jam & progress) |

Saat idle aplikasi tidak menjalankan timer dan tidak merender apa pun. Pemakaian RAM-nya setara
aplikasi WPF kecil pada umumnya (jauh di bawah aplikasi berbasis Electron).

## Kebutuhan

- Windows 10 versi 2004 (build 19041) atau lebih baru, atau Windows 11
- [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0) untuk menjalankan,
  atau .NET SDK 10 untuk build sendiri

## Cara menjalankan

### Opsi 1: unduh dari GitHub Actions

Setiap push otomatis di-build. Buka tab **Actions**, pilih run terakhir, lalu unduh artifact
**DynamicIsland-win-x64**. Isinya satu file `DynamicIsland.exe`, langsung jalankan.

### Opsi 2: build sendiri

```powershell
git clone <repo-ini>
cd dynamic-island
dotnet run --project src/DynamicIsland
```

Atau buka `DynamicIsland.sln` di Visual Studio 2022/2026 lalu tekan F5.

### Membuat file .exe

```powershell
# Satu file .exe kecil (perlu .NET Desktop Runtime 10 terpasang)
dotnet publish src/DynamicIsland -c Release -r win-x64 -p:SelfContained=false -p:PublishSingleFile=true -p:PublishReadyToRun=true -o publish

# Atau versi mandiri yang tidak perlu install runtime (ukuran file jauh lebih besar)
dotnet publish src/DynamicIsland -c Release -r win-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -o publish
```

Untuk laptop ARM (Snapdragon), ganti `win-x64` dengan `win-arm64`.

## Struktur kode

```
src/DynamicIsland/
├─ IslandWindow.xaml(.cs)   Tampilan island + state machine (idle / ringkas / aktivitas / expanded)
├─ Theme.xaml               Warna, ikon vektor, gaya tombol & menu klik kanan
├─ Animation/
│  ├─ Spring.cs             Fisika pegas (response + damping ratio, seperti SwiftUI)
│  └─ IslandAnimator.cs     Morph lebar, tinggi, dan sudut island
├─ Controls/
│  ├─ EqualizerBars.cs      Animasi gelombang "now playing"
│  └─ BatteryIcon.cs        Ikon baterai ala iOS
├─ Services/
│  ├─ MediaService.cs       Sesi media sistem (judul, cover, kontrol, timeline)
│  ├─ ArtPalette.cs         Mengambil warna aksen dari cover album
│  ├─ VolumeService.cs      Perubahan volume dari Core Audio
│  ├─ PowerMonitor.cs       Status baterai & charger
│  └─ ShellHooks.cs         Notifikasi fullscreen, daya, dan perubahan layar
├─ Interop/                 Deklarasi Win32 & Core Audio
└─ AppSettings.cs           Pengaturan (%AppData%\DynamicIsland\settings.json) & autostart
```

### Kustomisasi cepat

- **Ukuran tiap mode**: `SizeOf()` di `IslandWindow.xaml.cs`.
- **Rasa animasi**: `response` dan `damping` di `IslandAnimator.AnimateTo()`. Damping lebih kecil
  berarti pantulan lebih banyak.
- **Warna & ikon**: `Theme.xaml`.
- **Lama notifikasi**: argumen `TimeSpan` pada setiap pemanggilan `ShowActivity(...)`.

## Batasan

- Island hanya tampil di monitor utama.
- Popup volume bawaan Windows tetap muncul bersamaan dengan indikator di island.
- Notifikasi aplikasi (WhatsApp, email, dll.) belum didukung, karena API pembacanya mengharuskan
  aplikasi dipaketkan sebagai MSIX.
- Log error (kalau ada) tersimpan di `%AppData%\DynamicIsland\error.log`.
