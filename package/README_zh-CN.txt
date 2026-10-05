DLSS-NR on AMD for VaM 0.3.1（个人版本）
=========================================

本插件把 Virt-A-Mate 主摄像机的画面（桌面模式）交给一个独立 Direct3D 12 设备上的 AMD FSR 3.1 处理，
让 DLSS-NR-on-AMD 模组可以对 VaM 的画面进行神经渲染。场景以低于屏幕的分辨率渲染，再由 FSR 放大到
屏幕分辨率；VaM 自带的菜单以原生分辨率绘制在最上层。

运行要求
  - VaM 1.22，使用 Direct3D 11（默认设置），桌面模式。不支持 VR。
  - 适用于 VaM 的 BepInEx 5。
  - DLSS-NR-on-AMD 0.6.0（VaM.exe 所在文件夹里需要有 version.dll 和 dlssnr_on_amd.ini）。
    硬件要求与下载：https://github.com/danielblnc/DLSS-NR-on-AMD
  - 在 VaM 设置中关闭 MSAA：抗锯齿由 FSR 负责。

安装：将以下文件按原目录结构复制到 VaM 文件夹
  BepInEx\patchers\DlssNrAmd.Preloader.dll
  BepInEx\plugins\DlssNrAmd\DlssNrAmd.dll
  VaM_Data\Plugins\DlssNrAmdNative.dll
  VaM_Data\Plugins\DlssNrAmd\amd_fidelityfx_loader_dx12.dll
  VaM_Data\Plugins\DlssNrAmd\amd_fidelityfx_upscaler_dx12.dll
  VaM_Data\Managed\Mono.Cecil.dll   0.10.4 版，用于替换 VaM 自带的 0.9.6 版，这样 BepInEx 才能运行
                                    预加载补丁（preloader patcher）。替换前请先备份原文件。

使用方法
  Ctrl+Shift+N  开启 / 关闭效果。安装后默认关闭，之后会记住上次的开关状态；每次启动 VaM 后
                第一次开启大约需要 10 秒。
  Ctrl+Shift+Q  切换渲染分辨率：原生（Native）、质量（Quality，1/1.5）、平衡（Balanced，1/1.7）、
                性能（Performance，1/2）、超级性能（Ultra Performance，1/3）。
  Ctrl+Shift+P  打开设置面板（界面为英文）：包含以上选项、放大器（Upscaler：FSR 3.1 或 4.1）、
                曝光（Exposure）、纹理锐化（Texture sharpening）、运动矢量（Motion vectors），以及
                DLSS-NR 模组自身的设置：
                神经渲染开关（Neural rendering）、色调（Tone）、结构（Structure）、皮肤结构
                （Skin structure）、快速 / 参考模式（Fast / Reference）。模组的设置会写入
                dlssnr_on_amd.ini，模组运行中即时生效。
  全部设置（包括快捷键 ToggleKey、QualityKey、PanelKey）保存在：BepInEx\config\local.dlssnramd.cfg。
  快捷键请避开会被其他软件抢先占用的组合：例如 AMD 驱动软件（Adrenalin）占用了 Ctrl+Shift+O
  （性能监控叠加层），以及 Ctrl+Shift 加 S、I、J、U、E、C、M、G、L。

实测数据（RX 7800 XT，3840x2160，场景中一个角色）：关闭效果 184 fps；原生 5.3、质量 11.5、
平衡 13.9、性能 18.8、超级性能 30.1 fps。神经网络的开销随渲染分辨率而变化。

说明
  - VaM 不会为皮肤、衣物和模拟头发生成运动矢量，本插件会自行绘制，因此角色运动时不会拖影。
    头发的运动矢量是根据引导发丝（guide strands）近似生成的。
  - FSR 4.1（机器学习）在运动画面中效果更好。在 RX 7800 XT、4K 下每帧多花 3–4 毫秒：开启神经渲染时
    帧率约低 5%，单独使用 FSR 时从 185 降到 114 fps（性能档、一个角色）。
  - 在纯黑背景下，DLSS-NR 的结构（Structure）增强会把角色周围的黑色提亮成一圈淡淡的光晕（开启
    Windows 自动 HDR 时尤其明显）。在面板中把结构设为 0、皮肤结构设为 1，即可消除光晕并保留皮肤细节；
    代价是衣物和环境不再获得结构增强。
  - VaM 截图和存档缩略图所用的摄像机不受本插件影响。
  - 场景作者放在第 0 层（layer 0）上的 UI 会和场景一起经过放大处理。

许可证
  本插件：MIT 许可证（LICENSE.txt）。第三方组件均为 MIT 许可证（AMD FidelityFX SDK 的 DLL 与头文件、
  Unity 插件头文件、Mono.Cecil 0.10.4），详见 THIRD_PARTY_NOTICES.txt。
  源代码：https://github.com/sgtghost/DlssNrAmdForVAM
