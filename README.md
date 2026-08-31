<div align="center">
  <img src="https://github.com/user-attachments/assets/383a065e-b9a4-40b6-a06f-720857de883c" width="160px" />
  
  <h1>MajdataViewXL</h1>

  
  ![MajdataX Prisy](https://img.shields.io/badge/MajdataX-Prisy-50469C)
  [![GitHub Release](https://img.shields.io/github/v/release/re-poem/MajdataViewX?include_prereleases&sort=semver&display_name=release&label=version)](https://github.com/re-poem/MajdataViewX/releases)
  ![license GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)
  [![State-of-the-art Shitcode](https://img.shields.io/static/v1?label=State-of-the-art&message=Shitcode&color=7B5804)](https://github.com/trekhleb/state-of-the-art-shitcode)
</div>

# DISCLAIMER

- **This is a custom fork made by me (SkyLucky) with the intention of mimicking the UI and aesthetic of the arcade rhythm game maimai**
- **I'm not affiliated with MajdataTeam or Re_Poem and work on this fork for my own use**
- **This fork mostly made changes to the UI and such, and not anything too technical**

## Features
1. maimai Jacket assets (ripped from TRGUI branch of MajdataViewX)
   - Card Images
   - Lv flaps
   - Custom fonts
   - BPM text on card
   - Question mark object for use in utage
   - Tab for STD/DX indication
   - Tab for Utage kanji
       - Use by adding `[something]` at the start of the level text box. eg: `[J]14?`
2. Custom loading assets and animations
3. Custom commands for various functions
   - `chart_mode=STD` to change tab to STD (Default DX)
   - `gray_scale=true` to enable a custom shader for a grayscale loading animation
4. Modification of the UI
5. Add a judgement breakdown bar at the top of the playfield
6. Custom **ALL PERFECT** assets and animation

## Issues and Bugs report
If there are any issues or bugs, report the issue here. I don't guarantee that I can fix the bug, I will only look if the bug is from my mod or from the upstream MajdataViewX

## 相关链接 / Related Links

- MajdataX 的 QQ 群聊：361736398 (更快地反馈问题)
- Majdata 系 [官方Discord](https://discord.com/invite/AcWgZN7j6K)
- [MajdataNet](https://majdata.net/)
- [MajdataPlay](https://github.com/TeamMajdata/MajdataPlay_Build)

## 文档 / Documentation

- [中文 Wiki](https://github.com/LingFeng-bbben/MajdataView/wiki)
- [English Guide On Charting](https://rentry.co/maiguide#making-the-chart)
- [X新功能Wiki(不再维护，即将迁移)](https://github.com/re-poem/MajdataViewX/wiki)

## 导出说明 / Export Description

| 编码器 | 码控模式 | Low (0) | Medium (1) | High (2) | Ultra (3) |
|---|---|---|---|---|---|
| libx264   | CRF   | 28 | 23 | 18 | 14 |
| h264_nvenc | CQ    | 30 | 24 | 18 | 14 |
| h264_qsv  | ICQ   | 32 | 25 | 18 | 14 |
| h264_amf  | QVBR  | 30 | 24 | 18 | 14 |
| h264_mf   | 码率 (Mbps @1080p60) | 4 | 8 | 16 | 32 |

# Credits

### Main Programmer

- **[bbben](https://github.com/LingFeng-bbben)**
- **[Moying-moe](https://github.com/Moying-moe/maimaiMuriDetector)**
- **[Lezi](https://github.com/LeZi9916)**
- **RE_POEM**

### Contributors

- **Mirroring** from **[Wh1tyEnd](https://github.com/Wh1tyEnd)**
- **Hanabi** Effect from **青山散人**

### Special Thanks

- **Simai** developed by **[Celeca](https://twitter.com/formiku39854)**
- **MaiMuriDX** developed by **[Minepig](https://github.com/Minepig)**
- **MajdataMine** developed by **[RevoBleug](https://github.com/RevoBleug)**
- **MajSimai** developed by **[bbben](https://github.com/LingFeng-bbben/MajSimai)**

<br/>
<br/>

---

<p align="center">
Contributions welcome.⭐ If it helps, consider checking the original MajdataViewX and MajdataView repos.
</p>
