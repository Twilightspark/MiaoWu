---
name: office-wps
description: 用本机 WPS 读写 Office 文档（Word 为主，PPT 可出预览图）。当用户要起草、修改、排版、替换文字、插表格、更新目录、导出 PDF，或想确认文档改完是什么样时使用。Windows 7 + WPS 环境下这是唯一可靠的 Office 编辑通道。
---

# Office 文档编辑（WPS 通道）

`miaowu_office.exe` 是纯 .NET Framework 4.8 的本地命令行工具，通过 WPS 的 COM 接口真实打开、修改、保存文档。它只做三件事：**读结构**、**按计划改**、**出图给人看**。

## 0. 先定位程序

每条 `pwsh` 调用都是新进程，`$O` 不会保留，所以**每条命令前都贴一次这行定位**（它会从当前目录逐级向上找工作区的 `.miaowu`，在子目录里也能用）：

```powershell
$d=(Get-Location).Path; while($d -and -not (Test-Path "$d\.miaowu\skills\office-wps\bin\miaowu_office.exe")){$d=Split-Path $d -Parent}; $O="$d\.miaowu\skills\office-wps\bin\miaowu_office.exe"; if(-not(Test-Path $O)){$O="$env:USERPROFILE\.miaowu\skills\office-wps\bin\miaowu_office.exe"}
```

两处都找不到时**停下来问用户**技能装在哪里，不要自己下载或编译。

这是 MiaoWu 的**程序预设技能**：应用启动时已把这份内容释放到用户级技能目录（`%USERPROFILE%\.miaowu\skills\office-wps`），正常情况下第二条路径就能命中。用户在能力市场里停用只会影响模型可见性，不会删掉文件；`miaowu_office.exe` 随应用一起更新，不需要单独安装。

## 1. 第一次用：跑一次 doctor

```powershell
$d=(Get-Location).Path; while($d -and -not (Test-Path "$d\.miaowu\skills\office-wps\bin\miaowu_office.exe")){$d=Split-Path $d -Parent}; $O="$d\.miaowu\skills\office-wps\bin\miaowu_office.exe"; if(-not(Test-Path $O)){$O="$env:USERPROFILE\.miaowu\skills\office-wps\bin\miaowu_office.exe"}
& $O doctor --deep
```

看三件事：

- `wps.installed` 为 `false`：用户没装 WPS 或没注册，直接告诉他，别继续尝试。
- `wps.busy` 非空：WPS 正开着。**不要自动接管**，请用户保存并关闭所有 WPS 窗口再回来。
- `pdfium.available`：为 `true` 时 Word 文档也能出 PNG 预览；为 `false` 时 Word 只能交付 PDF（PPT 不受影响）。

同一个会话里 doctor 通过一次即可，不必每条命令都跑。

## 2. 标准工作流

下面每条命令都要在**同一次 `pwsh` 调用**里先贴上 §0 的定位行，再执行（`$O` 不跨调用保留）。

```
inspect  →  写计划 JSON  →  apply --dry-run  →  apply  →  inspect 复核  →  render 给用户看
```

**永远先 inspect。** 段落 `index` 只能从 inspect 结果里拿，不能凭上下文猜，也不能沿用上一轮对话里的旧数字。

动手前先对齐意图：用户只说「改一下这份文档」时，先 inspect，用一两句话说清文档结构和你的改法，再动手；不要一次改十几个段落。用户已经说清改法（例如「把第三章标题改成…」）时直接做，不必反复确认。

### 2.1 读结构

```powershell
& $O inspect --file 报告.docx --pretty --result .miaowu\cache\office\inspect-1.json
```

返回里最有用的字段：

| 字段 | 含义 |
|---|---|
| `paragraphCount` / `pageCount` / `tocCount` / `tableCount` | 文档规模 |
| `paragraphs[].index` | 1 开始的段落号，`index` 一律用它 |
| `paragraphs[].style` | 段落样式本地名（如 `标题 1`、`正文`） |
| `paragraphs[].text` | 折成一行的段落文本 |
| `paragraphs[].hash` | 段落文本短哈希，写进计划的 `expect` |
| `paragraphs[].outline` | 1–9 表示是几级标题 |
| `paragraphs[].inTable` | 该段在表格单元格里，**不要直接改** |
| `paragraphs[].toc` | 该段是目录条目，**不要直接改** |
| `paragraphs[].field` | 含域代码，改文本可能破坏域 |
| `headings` | 全文标题地图（长文档先看这个） |
| `styles` | 文档里在用的段落样式名，`style` 只能填这些 |
| `tables[].firstRow` | 每张表的首行内容，便于定位 |

长文档分段读：`--from 200 --max-paragraphs 200`。默认一次最多 400 段，超出会返回 `truncated: true` 和 `nextFrom`。

### 2.2 写计划

计划是一个 JSON 文件，放在工作区里（例如 `.miaowu\cache\office\plan.json`），用 `write` 工具写：

```json
{
  "operations": [
    { "op": "replace_paragraph", "index": 12, "expect": "9f2c1a77", "text": "新的这一段正文。" },
    { "op": "insert_paragraph_after", "index": 12, "text": "补充一句说明。" },
    { "op": "update_toc" },
    { "op": "export_pdf", "path": "报告-预览.pdf" }
  ]
}
```

### 2.3 先演练，再落笔

```powershell
& $O apply --file 报告.docx --plan .miaowu\cache\office\plan.json --dry-run --pretty
```

`--dry-run` 不写文件，只解析目标并逐条回显 `target.index` / `target.text`。**看到的目标不是你想改的那一段，就停下来改计划**，不要带着错的目标真跑。

确认无误再执行：

```powershell
& $O apply --file 报告.docx --plan .miaowu\cache\office\plan.json --result .miaowu\cache\office\apply-1.json
```

成功时返回 `backup`（原文件备份路径）、`validation.ok`、`sizeBefore/sizeAfter`，每条操作带 `ok`。

### 2.4 复核与出图

```powershell
& $O inspect --file 报告.docx --from 10 --max-paragraphs 20 --pretty
& $O render --file 报告.docx --out .miaowu\cache\office\preview --dpi 100
```

`render` 的结果：

- `.pptx`：直接出逐页 PNG，`pages[]` 里每页有 `path`，把图给用户看。
- `.docx`：出 PDF（`pdf` 字段）。`pdfium.dll` 存在时同时出逐页 PNG（`delivered: "png"`）；不存在时 `delivered: "pdf"`，告诉用户「已生成 PDF，需要图片预览请在技能目录的 bin 下放 pdfium.dll」。
- `.xlsx`：尽力出 PDF，WPS 表格导出不稳定，失败时 `warnings` 里会说明。

## 3. 操作速查

| op | 必填 | 可选 | 说明 |
|---|---|---|---|
| `replace_text` | `find`、`replace` | `index`/`anchor`、`all`、`caseSensitive`、`strategy` | 默认全文替换（`all: true`），保留段内局部格式 |
| `replace_paragraph` | `index` 或 `anchor`、`text` | `expect`、`style` | 整段换文字，保留段落样式与编号 |
| `insert_paragraph_after` | 同上 | `expect`、`style` | 在目标段后插入新段；不传 `style` 就继承目标段样式 |
| `insert_paragraph_before` | 同上 | `expect`、`style` | 在目标段前插入 |
| `delete_paragraph` | `index` 或 `anchor` | `expect` | 删整段（含段落标记） |
| `set_paragraph_style` | `index` 或 `anchor`、`style` | `expect` | 只改样式名，不动文字 |
| `insert_table` | `index` 或 `anchor`、`rows` | `expect`、`header`、`style` | `rows` 是二维数组，`header` 默认 `true`（首行加粗并重复表头） |
| `append_paragraph` | `text` | `style` | 追加到文档末尾 |
| `update_toc` | — | — | 更新所有目录域并重新分页；没有目录时返回 warning |
| `insert_toc` | `index` 或 `anchor` | `levels` | 在目标段前插入目录（按标题样式，`levels` 默认 3） |
| `set_property` | `name`、`value` | — | 文档属性：`Title`/`Subject`/`Author`/`Keywords`/`Comments`/`Company`/`Category` |
| `export_pdf` | — | `path` | 导出 PDF；`path` 相对文档所在目录 |

目标定位三选一：`index`（最稳）、`anchor`（段落文本包含该串）、`anchor` + `anchorMode: "exact"`（整段相等）。用 `anchor` 时可用 `occurrence` 取第几次匹配（默认 1）。

`expect` 填 inspect 给的 `hash`。哈希对不上会返回 `anchor_changed`，并且**文件一个字节都不会改**，这时重新 inspect 再生成计划。

数量上限：一次最多 60 条操作、表格最多 60 行 × 12 列。超了拆成多次。

## 4. 执行顺序（不用你操心，但要理解）

计划里的操作**不按你写的顺序执行**，而是：

1. 段落类操作按目标段落号**从后往前**执行（避免前面的编辑把后面的段落号顶偏）；
2. 文档级 `replace_text`；
3. 目录更新 / 插入；
4. `append_paragraph`；
5. `set_property`；
6. `export_pdf` 永远最后。

由此得到两条硬规则：

- **同一次 apply 里不能引用刚插入的段落。** 段落号在动手之前就解析完了。需要接着改新内容，就再来一轮 inspect + apply。
- 文档级 `replace_text` 作用在最终文本上（段落编辑之后），所以它也会替换掉你刚插入的句子里的同名字符串。

## 5. 安全边界（必须遵守）

1. **WPS 必须在关闭状态。** 返回 `wps_busy` 时请用户关闭 WPS，不要加 `--allow-running` 硬闯。
2. **每条都带 `expect`。** 这是防止「改错段落」的唯一硬保险。
3. **先 `--dry-run`。** 特别是 `delete_paragraph`、`insert_table`、`anchor` 定位。
4. **一条 apply 只做一类事。** 十条以上的计划拆开，出错时更好定位。
5. **不碰 `inTable` 和 `toc` 的段落。** 表格内容要改就重建表格（`insert_table`），目录用 `update_toc`。
6. **路径必须在工作区内。** 命令里传 `--workspace .` 更保险；工具会拒绝 `..`、UNC、`C:` 之外的越界路径。
7. **不要用 `Word.Application` / `Excel.Application` / `PowerPoint.Application`。** 那会绑定到 MS Office，本机没有。本工具只走 `KWPS` / `KET` / `KWPP`。
8. **不要为这件事装 Node、浏览器内核、MCP 服务或 OfficeCLI。** Win7 上它们跑不起来，本工具已覆盖需求。

## 6. 出错怎么办

| `error.code` | 含义 | 处置 |
|---|---|---|
| `wps_busy` | WPS 正在运行 | 请用户保存并关闭所有 WPS 窗口 |
| `wps_missing` | ProgID 未注册 | 请用户先手动打开一次 WPS，或修复安装 |
| `wps_timeout` | 超时被强杀 | 文档太大：缩小 `--max-paragraphs`、拆计划、或 `--timeout 240000` |
| `anchor_not_found` | 找不到锚点 | 重新 inspect，改用 `index` |
| `anchor_changed` | `expect` 哈希不匹配 | 重新 inspect 生成新计划 |
| `index_out_of_range` | 段落号越界 | 常见原因是在同一次 apply 里引用了新插入段落 |
| `style_not_found` | 样式名不存在 | 从 `detail.available` 里挑一个本地样式名 |
| `validation_failed` | 写回前完整性校验失败 | 已自动放弃，原文件未动；把计划拆小重试 |
| `file_locked` | 文件被占用 | 请用户关闭正在打开它的程序 |
| `outside_workspace` | 路径越界 | 换成工作区内的路径 |
| `pdfium_missing` | 没有 PDF 渲染库 | 只交付 PDF，或让用户放 pdfium.dll |
| `unsupported` | 格式不在范围内 | 只支持 `.docx/.doc/.docm`、`.xlsx/.xls`、`.pptx/.ppt` |

错误都带 `error.detail`，里面通常有 `hint`、`actual`、`available` 这类可直接行动的信息，照着做，不要原样重试同一条命令。

## 7. 性能与低配现实

- 每条命令都要启动一次 WPS，冷启动 5–10 秒，这是主要耗时。**能合并的命令就合并**（一次 inspect、一次 apply 带多条操作）。
- 每条 `pwsh` 命令用户都要点一次「允许」。把「dry-run + 确认无误后的 apply」分开是必要的，但「inspect 两个文件」这种可以写在同一次调用里，用 `;` 或换行连接。
- 目标机是 Windows 7 + 3.75GB 内存：单文档建议 ≤50MB、段落 ≤5000。超大文档分段处理。
- 同一次只能处理一个文档，不要并发跑两个 `miaowu_office`。
- PPT 导出超过 40 页会截断，需要后面的页用 `--page N` 指定。

## 8. 交付给用户时说什么

1. 改了什么（按段落号 / 原文 → 新文）。
2. 备份在哪：`apply` 返回的 `backup` 路径，以及怎么回滚：
   ```powershell
   & $O restore --file 报告.docx --backup 报告.miaowu-backup-20261128-115151.docx
   ```
   `restore --latest` 取最近一次备份，`restore --list` 列出来。
3. 预览图或 PDF 路径，让用户自己确认版式。
4. 如果有 warning（没有目录、表格样式没设置成功、只交付了 PDF），明确说出来，不要假装全绿。

## 9. 能力边界（先说清楚，别硬撑）

- **Word 编辑是完整的**：段落、样式、表格、目录、文档属性、PDF。
- **PPT**：只能读页数并导出 PNG 预览，**不能改内容**。要改 PPT 请让用户手动改，或只做预览。
- **Excel**：只能尽力导出 PDF 供查看，**不能读单元格、不能改数据**。用户要改表格数据，请说明这个技能暂不支持，建议他手动改或用其他方式。
- 不支持密码保护的文档（会以 `wps_error` 失败，提示用户先解密）。
- 不支持修订模式下的精确追踪：改动会直接落在正文里。
- 不保证导出 PDF 与 WPS 界面里 100% 一致（字体、分页可能因机器而异）。
