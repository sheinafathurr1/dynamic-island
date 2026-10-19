# Dynamic Island untuk Windows

Island / notch ala iPhone & MacBook di bagian atas layar Windows. Dibuat native (C# WPF, .NET 10)
supaya **ringan**: tidak ada Electron atau browser tersembunyi, hampir semuanya digerakkan event dari
Windows, dan tidak ada render saat island diam.

<p align="center">
  <img src="docs/preview.png" alt="Tangkapan layar Dynamic Island di Windows: idle, musik dan lirik, panggilan, notifikasi, screenshot, unduhan, cuaca dan agenda, kontrol, sistem, timer, titipan, pengaturan, dan menu klik kanan" width="760">
</p>

<sub>Tangkapan layar asli dari tes UI otomatis di GitHub Actions (Windows). Musiknya sesi media
buatan tes dengan lagu domain publik, notifikasinya toast sungguhan, dan agendanya file iCal contoh.</sub>

## Fitur

- **Cahaya suasana**: di dalam island ada cahaya lembut yang warnanya mengikuti apa yang sedang
  tampil. Halaman Hari ini menjadi "langit" kecil: oranye-pink saat fajar, biru saat siang,
  oranye saat senja, ungu-indigo di malam hari, dan biru keabuan saat hujan. Timer oranye (merah di
  10 detik terakhir, dan berdenyut saat waktu habis), panggilan hijau (merah saat dibisukan),
  pesan memakai warna aplikasinya (WhatsApp hijau, Discord ungu), musik memakai warna cover. Titik
  halaman yang aktif ikut berwarna. Cahayanya hanya di dalam bentuk island, jadi tidak menghalangi
  klik di sekitarnya.
- **Tepi kaca & cahaya kursor**: garis tipis berkilau di tepi island membuatnya terlihat seperti
  kaca hitam, bukan potongan hitam datar; saat island terbuka, cahaya samar mengikuti kursor.
- **Sapaan**: saat island mulai, muncul "Selamat pagi/siang/sore/malam" dengan tanggal dan ikon
  matahari atau bulan.
- **Kucing mini**: kucing hitam kecil menggantung di bawah island dengan kaki depannya. Saat sepi ia
  tidur (bernapas, "z" melayang), bangun dan melirik saat kursor mendekat, menggoyangkan kepala saat
  musik diputar, dan keluar hati kalau diklik (dielus). Ia melompat pergi saat island terbuka atau
  menampilkan notifikasi, lalu turun lagi.
- **Island kenyal**: tarik island ke bawah atau ke samping dan ia memanjang seperti jelly, lalu
  memantul kembali saat dilepas (kucingnya ikut tertarik); klik dua kali untuk membuatnya bergoyang.
  Menarik ke samping di island yang terbuka tetap menggeser halaman.
- **Carousel**: island yang terbuka berisi halaman-halaman yang bisa digeser, dengan titik
  pagination ala iOS: 📞 **Panggilan** (saat mikrofon dipakai) → 🎵 **Musik** → 🎤 **Lirik** (kalau ada
  media) → ⏱ **Timer** → 📅 **Hari ini** → 🎛 **Kontrol** → 📊 **Sistem** → 📥 **Titipan** (kalau ada file). Geser dengan **scroll**, swipe dua jari ke samping di touchpad,
  klik-tarik (atau swipe di layar sentuh), atau klik titiknya. Halaman bisa "dilempar", memantul di
  ujung, halaman samping mengecil dan memudar, dan tinggi island ikut menyesuaikan.
- **Shift + scroll untuk volume**: di atas island (kecil maupun terbuka), 2% per langkah; klik
  tengah (klik roda mouse) untuk mute. Saat island terbuka, bar volume kecil muncul di tempat titik
  pagination.
- **Lirik lagu** (Spotify, YouTube Music, Apple Music, dll.): halaman 🎤 Lirik bergaya karaoke —
  baris yang sedang dinyanyikan menyala dan bergulir otomatis, klik sebuah baris untuk loncat ke
  bagian itu. Saat island kecil, baris lirik yang sedang dinyanyikan tampil langsung di island.
  Lirik diambil dari [LRCLIB](https://lrclib.net) (database lirik komunitas, gratis, tersinkron)
  berdasarkan judul/artis/album/durasi, karena Spotify tidak menyediakan API lirik untuk aplikasi
  lain. Bisa dimatikan di klik kanan → **Lirik**.
- **Titipan file (seperti NotchDrop)**: seret file atau folder ke island — island membuka area drop
  dengan garis putus-putus yang bergerak. File muncul sebagai thumbnail di halaman Titipan; seret
  keluar ke folder/aplikasi mana pun (ikon di judul = bawa semua sekaligus), klik untuk membuka,
  × untuk melepas. Yang disimpan hanya lokasinya (tidak ada salinan), tetap ada setelah restart,
  dan badge kecil di island menunjukkan jumlahnya.
- **Now Playing**: Spotify, YouTube/Chrome/Edge, Media Player, dan semua aplikasi yang muncul di
  kontrol media Windows. Mode ringkas menampilkan cover album + equalizer yang warnanya diambil dari
  cover. Hover atau klik untuk membuka panel penuh: judul, artis, progress bar (bisa diklik untuk
  *seek*), serta tombol previous / play-pause / next.
- **Equalizer mengikuti suara asli**: batang equalizer bergerak sesuai musik yang benar-benar keluar
  dari speaker/headphone (bass, mid, treble), bukan animasi rekaman. Yang didengar hanya output
  audio (*loopback*), tidak pernah mikrofon, dan tidak ada yang disimpan. Kalau tidak ada suara
  (player di-mute, atau musik keluar di perangkat lain) equalizer kembali ke animasi biasa.
- **Island ringkas yang hidup**: garis tipis berwarna cover di pinggir island yang "bernapas"
  mengikuti dentuman bass, dan baris lirik yang bergulir naik saat berganti.
- **Notifikasi aplikasi**: pesan WhatsApp, Discord, Telegram, email, dll. muncul sebentar di island
  dengan foto pengirim (kalau aplikasinya menyertakan), ikon dan nama aplikasi. Klik untuk membuka
  aplikasinya; arahkan kursor untuk menahannya. Mengikuti aturan Windows: tidak muncul saat
  *Do not disturb* / Fokus aktif atau untuk aplikasi yang notifikasinya dimatikan. Isi pesan bisa
  disembunyikan (klik kanan → Notifikasi → Tampilkan isi pesan).
  **Tidak dobel**: setelah island menampilkan notifikasi sebuah aplikasi, banner bawaan Windows untuk
  aplikasi itu dimatikan (saklar yang sama dengan Settings → Notifikasi → aplikasi → *Show
  notification banners*), jadi pesan berikutnya hanya muncul sekali, di island. Pesannya tetap masuk
  ke pusat notifikasi Windows. Saat island ditutup, banner Windows otomatis dinyalakan lagi.
- **Tidak menghalangi**: saat tidak ada aktivitas, island menjadi garis tipis di tepi paling atas
  layar, jadi tab browser dan tombol di bawahnya tetap bisa dipakai; dorong kursor ke tengah atas
  untuk membukanya. Island juga baru terbuka kalau kursor *berhenti* di atasnya, bukan sekadar lewat.
  Opsi **Jangan tutupi jendela** menyisakan ruang di atas layar seperti taskbar, sehingga jendela
  yang di-maximize mulai di bawah island.
- **Lagu baru**: notifikasi singkat berisi cover, judul, dan artis setiap kali lagu berganti.
- **Timer & Pomodoro**: mulai dari halaman Timer (5–45 menit) atau menu klik kanan (1–60 menit).
  Island ringkas menampilkan ring hitung mundur + sisa waktu (berdampingan dengan cover kalau musik
  jalan). Halaman Timer punya ring yang menyala dengan titik terang di ujungnya, jam selesainya, serta
  pause, +1 menit, dan stop; 10 detik terakhir berubah merah. Saat waktu habis island berdenyut dan
  berbunyi; klik untuk menutup.
- **Indikator mikrofon & kamera**: titik oranye (mikrofon) dan hijau (kamera) seperti iPhone,
  plus notifikasi berisi nama aplikasi yang memakainya (Zoom, Discord, Chrome, dll.).
- **Ganti perangkat audio**: notifikasi saat output pindah, misalnya earbuds Bluetooth tersambung,
  lengkap dengan nama perangkat dan ikon headphone/speaker.
- **Volume**: indikator volume & mute saat volume diubah (tombol keyboard, tray, atau aplikasi).
- **Baterai**: "Mengisi daya" saat charger dicolok, "Menggunakan baterai" saat dicabut,
  "Baterai lemah" ketika baterai turun ke 20% dan 10%, serta status penghemat baterai (kuning).
- **Pil panggilan**: saat Zoom, Meet, Teams, Discord, dll. memakai mikrofon, island ringkas berubah
  jadi pil hijau berisi nama aplikasi dan durasi panggilan. Halaman Panggilan punya tombol
  **Bisukan** yang mematikan mikrofon untuk semua aplikasi sekaligus (pil jadi merah), dan klik
  ikon mikrofonnya membawa aplikasi rapat ke depan.
- **Preview screenshot**: setelah Win+Shift+S, PrtSc, atau Win+PrtSc, island menampilkan thumbnail
  screenshot. Seret langsung ke WhatsApp/Explorer, buka, tampilkan di folder, atau titipkan.
- **Unduhan selesai**: saat file di folder Downloads selesai diunduh (Chrome, Edge, Firefox, dll.),
  island menampilkan nama, ukuran, dan ikonnya, dengan tombol yang sama.
- **Kontrol**: tombol bulat untuk Wi-Fi, Bluetooth, mute mikrofon, mute suara, dan mode gelap/terang.
- **Sistem**: CPU (dengan grafik kecil), memori, kecepatan internet ↓↑, dan sisa disk; hanya dihitung
  selama halamannya terbuka.
- **Cuaca** di halaman Hari ini: suhu dan kondisi sekarang plus 3 jam ke depan, dari
  [Open-Meteo](https://open-meteo.com) (gratis, tanpa akun). Isi kota di Pengaturan, atau biarkan
  kosong untuk memakai lokasi Windows.
- **Hari ini**: jam, tanggal, baterai, perangkat audio, kalender minggu ini (hari ini dilingkari),
  dan **agenda**: acara yang sedang/akan berlangsung hari ini dan besok, plus titik di hari yang ada
  acaranya. Agenda diambil dari link iCal Google Calendar, Outlook, atau iCloud (klik kanan →
  **Kalender**, atau klik kartu agendanya), diperbarui tiap 15 menit, termasuk acara berulang.
- **Dua gaya**: *Island* (pil melayang) atau *Notch* (menempel di tepi atas dengan lekukan seperti
  MacBook).
- **Otomatis sembunyi** saat ada aplikasi fullscreen (game, video, presentasi).
- Animasi pegas (*spring*) dengan sedikit pantulan seperti aslinya, tajam di semua skala DPI.
- Tidak mencuri fokus saat diklik dan tidak muncul di taskbar maupun Alt+Tab.

Klik kanan island untuk menu singkat: **Timer**, **Kalender**, **Pengaturan…**, dan keluar.
Jendela **Pengaturan** berisi semua opsi dalam bentuk saklar: gaya island/notch, cahaya suasana,
sapaan, kucing, island kenyal, garis tipis saat diam, jangan tutupi jendela, halaman Kontrol/Sistem, cuaca dan kotanya, lirik dan equalizer, semua
jenis notifikasi (aplikasi, screenshot, unduhan, panggilan, lagu, volume, perangkat audio), link
kalender, dan jalankan saat Windows mulai. Perubahan langsung berlaku.

## Kenapa ringan

| Bagian | Cara kerjanya |
| --- | --- |
| Animasi bentuk | Simulasi pegas yang hanya terhubung ke render loop **selama bergerak**, lalu lepas lagi |
| Equalizer | 30 fps, dan hanya saat musik diputar **dan** terlihat di layar; pendengar audio (loopback + FFT kecil) juga hanya aktif saat itu, dan bisa dimatikan |
| Media | Event dari `GlobalSystemMediaTransportControls`, tanpa polling |
| Volume | Callback Core Audio (`IAudioEndpointVolumeCallback`), tanpa polling |
| Baterai | Pesan `WM_POWERBROADCAST` dari Windows, tanpa polling |
| Fullscreen | Notifikasi AppBar `ABN_FULLSCREENAPP` dari shell, tanpa polling |
| Mikrofon & kamera | Notifikasi perubahan registry (`RegNotifyChangeKeyValue`), tanpa polling |
| Timer / Pomodoro | Satu alarm sekali-jalan di akhir hitungan; tampilan hanya di-update selama terlihat |
| Update tampilan | Hanya untuk halaman yang sedang terlihat (jam, progress lagu, hitung mundur) |
| Carousel | Pegas yang hanya terhubung ke render loop selama halaman bergeser |
| Titipan file | Hanya path yang disimpan; thumbnail diambil sekali dari Explorer saat tampil |
| Lirik | Satu request per lagu (di-cache); baris berikutnya dijadwalkan tepat saat mulai, bukan dicek terus-menerus |
| Notifikasi aplikasi | Dibaca saat file database notifikasi Windows berubah, plus satu query kecil tiap 3 detik sebagai cadangan |
| Kalender | Satu unduhan file iCal tiap 15 menit; acara berulang dihitung dalam milidetik |
| Cuaca | Satu request kecil ke Open-Meteo tiap 30 menit |
| Screenshot & unduhan | Notifikasi clipboard dan perubahan folder dari Windows, tanpa polling |
| Pil panggilan | Notifikasi registry privasi yang sama dengan indikator mikrofon; jam durasi hanya jalan selama terlihat |
| Sistem | Dibaca sekali per detik hanya selama halaman Sistem terbuka |
| Cahaya suasana | Dua gradien statis; warnanya hanya dianimasikan (setengah detik) saat halaman atau aktivitas berganti |
| Kucing | Digambar dengan beberapa bentuk vektor; gerakannya dibatasi 12 fps saat tidur (24 saat menari), posisi kursor dicek 4× per detik, dan semuanya berhenti total saat kucing tersembunyi atau dimatikan |
| Island kenyal | Pegas yang hanya berjalan selama ditarik atau bergoyang |

Saat idle aplikasi tidak menjalankan timer dan tidak merender apa pun. Pemakaian RAM-nya setara
aplikasi WPF kecil pada umumnya (jauh di bawah aplikasi berbasis Electron).

## Kebutuhan

- Windows 10 versi 2004 (build 19041) atau lebih baru, atau Windows 11
- [.NET Desktop Runtime 10](https://dotnet.microsoft.com/download/dotnet/10.0) untuk menjalankan,
  atau .NET SDK 10 untuk build sendiri

## Cara menjalankan

### Opsi 1: unduh dari GitHub Actions

Setiap push otomatis di-build dan dites di Windows. Buka tab **Actions**, pilih run terakhir, lalu
unduh artifact **DynamicIsland-win-x64**. Isinya satu file `DynamicIsland.exe`, langsung jalankan.
Artifact **screenshots** berisi tangkapan layar setiap tampilan dari tes UI otomatis.

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
├─ IslandWindow.xaml(.cs)   Tampilan island, state machine, dan carousel halaman
├─ Theme.xaml               Warna, ikon vektor, gaya tombol & menu klik kanan
├─ Animation/
│  ├─ Spring.cs             Fisika pegas (response + damping ratio, seperti SwiftUI)
│  ├─ SpringValue.cs        Satu nilai berpegas (posisi carousel)
│  └─ IslandAnimator.cs     Morph lebar, tinggi, dan sudut island
├─ Controls/
│  ├─ EqualizerBars.cs      Gelombang "now playing" (suara asli atau animasi)
│  ├─ ProgressRing.cs       Ring hitung mundur timer
│  ├─ WeatherIcon.cs        Ikon cuaca vektor (matahari, bulan, awan, hujan, petir...)
│  ├─ Sparkline.cs          Grafik kecil CPU
│  └─ BatteryIcon.cs        Ikon baterai ala iOS
├─ Services/
│  ├─ MediaService.cs       Sesi media sistem (judul, cover, kontrol, timeline)
│  ├─ ArtPalette.cs         Mengambil warna aksen dari cover album
│  ├─ VolumeService.cs      Perubahan volume dari Core Audio
│  ├─ PowerMonitor.cs       Status baterai, charger & penghemat baterai
│  ├─ PrivacyMonitor.cs     Aplikasi yang sedang memakai mikrofon / kamera
│  ├─ TimerService.cs       Timer & Pomodoro
│  ├─ ShelfService.cs       Daftar file titipan (shelf.json)
│  ├─ LyricsService.cs      Lirik tersinkron dari LRCLIB + parser LRC
│  ├─ AudioSpectrum.cs      Mendengarkan output audio (WASAPI loopback) untuk equalizer
│  ├─ SpectrumAnalyzer.cs   FFT → level bass / mid / treble dengan gain otomatis
│  ├─ NotificationWatcher.cs  Notifikasi aplikasi lain dari database notifikasi Windows
│  ├─ ToastPayload.cs       Membaca isi XML notifikasi toast
│  ├─ CalendarService.cs    Mengambil & memperbarui feed iCal
│  ├─ IcsCalendar.cs        Parser iCal: zona waktu, acara seharian, acara berulang
│  ├─ WeatherService.cs     Cuaca dari Open-Meteo (kota atau lokasi Windows)
│  ├─ WeatherForecast.cs    Membaca jawaban Open-Meteo + kode cuaca → ikon & teks
│  ├─ ScreenshotWatcher.cs  Screenshot baru (clipboard Snipping Tool & folder Screenshots)
│  ├─ DownloadWatcher.cs    Unduhan yang selesai di folder Downloads
│  ├─ MicrophoneMute.cs     Bisukan mikrofon untuk semua aplikasi
│  ├─ QuickControls.cs      Wi-Fi, Bluetooth, mode gelap
│  ├─ SystemStats.cs        CPU, memori, jaringan, disk
│  └─ ShellHooks.cs         Notifikasi fullscreen, daya, dan perubahan layar
├─ CalendarDialog.xaml(.cs) Jendela untuk menempel link iCal
├─ SettingsWindow.xaml(.cs) Jendela Pengaturan (semua opsi sebagai saklar)
├─ Interop/                 Deklarasi Win32, Core Audio, SQLite bawaan Windows & thumbnail Explorer
└─ AppSettings.cs           Pengaturan (%AppData%\DynamicIsland\settings.json) & autostart
tools/ui-smoke-test.ps1     Tes UI otomatis di CI (menjalankan app, klik-klik, ambil screenshot)
```

### Kustomisasi cepat

- **Ukuran tiap mode**: `SizeOf()` di `IslandWindow.xaml.cs`.
- **Rasa animasi**: `response` dan `damping` di `IslandAnimator.AnimateTo()`. Damping lebih kecil
  berarti pantulan lebih banyak.
- **Warna & ikon**: `Theme.xaml`; warna cahaya suasana per halaman di `CurrentMood()` dan
  `SkyMood()`.
- **Lama notifikasi**: argumen `TimeSpan` pada setiap pemanggilan `ShowActivity(...)`.

## Batasan

- Island hanya tampil di monitor utama.
- Popup volume bawaan Windows tetap muncul bersamaan dengan indikator di island.
- Indikator mikrofon/kamera membaca catatan privasi Windows, jadi aplikasi yang izinnya dimatikan
  di Settings → Privacy tidak akan terdeteksi (karena memang tidak bisa memakai perangkatnya).
- Lirik hanya tersedia untuk lagu yang ada di LRCLIB; judul/artis/album lagu yang diputar dikirim
  ke lrclib.net untuk mencarinya (matikan di klik kanan → Lirik kalau tidak mau).
- Notifikasi aplikasi dibaca dari database notifikasi Windows, karena API resminya hanya untuk
  aplikasi yang dipaketkan sebagai MSIX. Formatnya tidak didokumentasikan, jadi update Windows bisa
  saja mengubahnya; kalau itu terjadi, fitur ini diam saja (tidak mengganggu fitur lain). Klik
  notifikasi membuka aplikasinya, belum langsung ke chat tertentu.
- Pesan pertama dari sebuah aplikasi masih muncul dobel (banner Windows-nya baru dimatikan setelah
  itu). Kalau island dihentikan paksa (Task Manager), banner Windows untuk aplikasi tersebut tetap
  mati sampai island jalan dan ditutup lagi lewat **Keluar**, atau sampai dinyalakan manual di
  Settings → Notifikasi.
- Aplikasi Kalender bawaan Windows sudah dihentikan Microsoft dan Outlook baru tidak membagikan
  agendanya ke aplikasi lain, jadi agenda memakai link iCal. Link itu rahasia: hanya disimpan di
  `settings.json` di PC ini.
- Tombol Bisukan di pil panggilan mematikan mikrofon default Windows (semua aplikasi), bukan tombol
  mute di dalam Zoom/Meet. Wi-Fi dan Bluetooth hanya bisa diatur di PC yang punya perangkatnya.
- Cuaca tanpa kota memakai lokasi Windows; kalau "Izinkan aplikasi desktop mengakses lokasi" mati,
  isi kota di Pengaturan.
- Equalizer suara asli mendengarkan perangkat output default; kalau Spotify diarahkan ke perangkat
  lain, equalizer memakai animasi biasa.
- Log error (kalau ada) tersimpan di `%AppData%\DynamicIsland\error.log`.
