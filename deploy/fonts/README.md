# 自托管字体

后台与小程序共用同一份字体，保证所有访客看到一致的排版。

## 为什么不靠系统字体栈

规格里的字体名在不同电脑上解析结果不同。实测 Windows 上
`-apple-system` / `SF Pro Text` / `PingFang SC` **都不存在**，
于是所有字符落到 `Microsoft YaHei` —— 雅黑是中文黑体，
它的数字又宽又方（8 位数字 188px，Segoe UI 只要 173px），
满屏数字的后台因此「一眼方正」；换台 Mac 又完全是另一个样子。

对**网站**来说这不可接受：同一份后台，在不同人电脑上应该是同一张脸。

## 来源与授权

- 字体：**Noto Sans SC**（可变字体，wght 100–900）
- 授权：**SIL Open Font License 1.1**，允许再分发与嵌入
- 原始下载地址：<https://fonts.google.com/noto/specimen/Noto+Sans+SC>

再分发字体必须附带 OFL 许可，本目录的字体由 `scripts/build-webfont.py` 生成，
源头字体的许可见上述链接。改动字体或字符集时请一并核对许可。

## 重新生成

```powershell
python -m pip install fonttools brotli
python scripts/build-webfont.py
```

把源字体 `NotoSansSC-VF.ttf` 放在本目录，脚本会裁出
「ASCII + 拉丁补充 + 常用符号 + CJK 标点 + 全角 + GB2312 全部汉字」
共约 8378 字，输出 `apps/admin-vue/public/fonts/NotoSansSC-subset.woff2`。

16.95MB -> 1.87MB，保留 wght 轴，一个文件覆盖 400~900 全字重。

## 字符集为什么用 GB2312 枚举而不是「常用 3500 字」清单

人工清单换个词就可能漏字，漏掉的字会**静默回退到系统字体** ——
正是这次要消灭的问题。GB2312 由编码标准定义，完整且可复现。

## 体积还能再压吗

可以：像 Google Fonts 那样按 `unicode-range` 切成上百个分片，
首屏只下载用到的几片，总量能降到 300KB 左右。
代价是文件数上百、维护成本高。当前 1.87MB 对内网后台可接受，先不做。
