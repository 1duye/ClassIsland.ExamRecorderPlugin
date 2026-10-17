# 图标

<img src="../icon.png" alt="ExamAware2联系人" width="96">

白底 + 深蓝线条的平面简约线性图标。ClassIsland 端「ExamAware2联系人」与 ExamAware 端「自动放映」为一套：同一台"屏幕 + 播放三角"，只在右下角换一个小元素（倒计时弧 / 对勾徽标）。

## 文件

| 文件 | 用途 |
| --- | --- |
| `icon.png` | 256×256，插件实际使用的图标 |
| `design/icon-1024.png` | 1024×1024，改图用 |

## 出图与后处理

即梦 AI 生成 2048×2048，然后：

1. 取"深色线条"（亮度 < 150）的包围盒 —— 这一步同时把生成水印排除在外；
2. 按较长边 ×1.28 做正方形留白，居中贴到**纯白**画布；
3. 导出 1024 与 256 两个尺寸。

提示词（可整段复用）：

```text
Design a flat minimalist line icon on a pure white background.

Exactly three stroked elements and nothing else: a rounded rectangle screen outline (wider than tall); a play triangle outline with rounded corners, centered inside the screen; and a stroked rounded square docked on the screen's lower-right corner carrying a single stroked check mark.

Style: flat minimalist LINE art. Strokes only — nothing is filled, no solid masses anywhere.
Stroke color: one single dark color, deep navy #12294D. No second color, no tint, no gradient inside the stroke.
Stroke width: one uniform width of about 40 px at 1024 px everywhere; perfectly round caps and perfectly round joins; even, crisp, confident lines.
Canvas: square 1024x1024. The mark is centered, occupies about 56 percent of the canvas, with at least 22 percent empty margin on every side.
Background: one pure solid #FFFFFF, completely flat and uniform — no gradient, no vignette, no texture, no shadow, no outer frame, no visible border.
Forbidden: any text, letters, digits or characters of any language; watermark; signature; any second color; any fill; double outlines; dashed or dotted lines; hairlines; glow; blur; drop shadow; bevel; 3D; reflection; texture; noise; hatching; more than three stroked elements; floating decorative dots; sparkles; device mockup; outer frame; second icon; background scenery.
```

与「自动放映」同一套构造，只把右下角换成「记录/确认」徽标。ClassIsland 读取插件目录下的 `icon.png`，改图后重新 `dotnet build -c Release` 并替换部署目录里的 `icon.png` 即可。
