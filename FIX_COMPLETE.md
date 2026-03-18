# CSV源文件导出功能 - 问题修复完成

## ✅ 修复完成

已成功修复CSV源文件导出功能，编译通过，无错误无警告！

## 🔍 问题原因

之前只添加了 `using Luban.CsvSource;` 引用，但忘记添加实际的数据捕获和导出代码。现在已经完整实现。

## 📝 您的命令分析

```batch
set LUBAN_DLL=Tools\Luban\Luban.dll
set CONF_ROOT=Tools\game.conf
set GEN_DATA_SOURCE=..\..\client\game\Assets\Publish\configs\table
set GEN_CODE_SOURCE=..\..\client\game\Assets\Scripts\Configs\Gen

dotnet %LUBAN_DLL% ^
    -t client ^
    -c cs-bin ^
    -d bin ^
    -i dev test ^
    -f ^
    --conf %CONF_ROOT% ^
    -x cs-bin.outputCodeDir=%GEN_CODE_SOURCE% ^
    -x bin.outputDataDir=%GEN_DATA_SOURCE% ^
    -x csvSourceOutputDir=./csv_source ^
    -x dataExporter=tag-split
```

**命令解析：**
- `-t client`: 目标为client
- `-c cs-bin`: 代码目标为cs-bin
- `-d bin`: 数据目标为bin
- `-i dev test`: 包含dev和test标签
- `-f`: 强制加载表数据
- `-x csvSourceOutputDir=./csv_source`: **CSV源文件输出目录**
- `-x dataExporter=tag-split`: 使用tag-split数据导出器

## ✅ 现在应该可以正常工作了

重新编译后，您的命令应该能够正常生成CSV源文件到 `./csv_source` 目录。

## 🧪 验证步骤

### 1. 复制新编译的DLL

首先需要将新编译的DLL复制到您的Tools目录：

```batch
REM 复制核心DLL
copy /Y E:\Projects\luban\src\Luban.Core\bin\Debug\net8.0\Luban.Core.dll Tools\Luban\
copy /Y E:\Projects\luban\src\Luban.DataLoader.Builtin\bin\Debug\net8.0\Luban.DataLoader.Builtin.dll Tools\Luban\
copy /Y E:\Projects\luban\src\Luban\bin\Debug\net8.0\Luban.dll Tools\Luban\

REM 如果有其他依赖的DLL也需要复制
copy /Y E:\Projects\luban\src\Luban.CSharp\bin\Debug\net8.0\Luban.CSharp.dll Tools\Luban\
copy /Y E:\Projects\luban\src\Luban.DataTarget.Builtin\bin\Debug\net8.0\Luban.DataTarget.Builtin.dll Tools\Luban\
```

### 2. 运行您的导表命令

```batch
set LUBAN_DLL=Tools\Luban\Luban.dll
set CONF_ROOT=Tools\game.conf
set GEN_DATA_SOURCE=..\..\client\game\Assets\Publish\configs\table
set GEN_CODE_SOURCE=..\..\client\game\Assets\Scripts\Configs\Gen

dotnet %LUBAN_DLL% ^
    -t client ^
    -c cs-bin ^
    -d bin ^
    -i dev test ^
    -f ^
    --conf %CONF_ROOT% ^
    -x cs-bin.outputCodeDir=%GEN_CODE_SOURCE% ^
    -x bin.outputDataDir=%GEN_DATA_SOURCE% ^
    -x csvSourceOutputDir=./csv_source ^
    -x dataExporter=tag-split
```

### 3. 检查输出

运行后，您应该能看到：

```
csv_source/
├── table1.csv
├── table2.csv
└── ...
```

### 4. 查看日志

在控制台输出中，您应该能看到类似的日志：

```
[INFO] 开始导出CSV源文件到: ./csv_source
[DEBUG] 导出CSV源文件: xxx.xlsx -> ./csv_source/xxx.csv
[INFO] CSV源文件导出完成，共导出 X 个文件
```

## 🔧 调试技巧

如果还是没有输出，可以尝试以下方法：

### 1. 启用详细日志

修改 `nlog.xml` 配置，将日志级别设置为 `Trace`：

```xml
<logger name="*" minlevel="Trace" writeTo="console" />
```

### 2. 检查配置是否生效

在日志中搜索 `csvSourceOutputDir`，确认配置被正确读取。

### 3. 使用绝对路径

尝试使用绝对路径而不是相对路径：

```batch
-x csvSourceOutputDir=C:\full\path\to\csv_source
```

### 4. 简化测试

创建一个最简单的测试命令：

```batch
dotnet Tools\Luban\Luban.dll ^
    -t client ^
    -f ^
    --conf Tools\game.conf ^
    -x csvSourceOutputDir=./test_csv
```

## 📊 CSV文件格式示例

生成的CSV文件格式如下：

```csv
## Sheet: Sheet1
id,name,level,exp
##type,int,string,int,int
##desc,ID,名称,等级,经验值
1,新手剑,1,0
2,铁剑,5,100
3,钢剑,10,500
```

## 🎯 实现的功能

1. **自动捕获数据**：在Excel数据加载时自动捕获原始数据
2. **保留元信息**：保留字段名、类型、描述等信息
3. **标准CSV格式**：输出标准CSV格式，便于Git对比
4. **自动转义**：自动处理包含逗号、引号、换行的字段
5. **多Sheet支持**：支持Excel中的多个Sheet

## 📋 修改的文件清单

1. **Luban.Core/BuiltinOptionNames.cs** - 添加配置项
2. **Luban.Core/CsvSource/CsvSourceExporter.cs** - CSV导出器实现
3. **Luban.Core/Pipeline/DefaultPipeline.cs** - 集成到Pipeline
4. **Luban.DataLoader.Builtin/Excel/ExcelRowColumnDataSource.cs** - 数据捕获

## ⚠️ 注意事项

1. **必须更新DLL**：需要将新编译的DLL复制到您的Tools目录
2. **路径问题**：确保输出路径有写入权限
3. **数据加载**：只有在数据加载时才会生成CSV，确保使用了 `-f` 参数或指定了 `-d` 参数
4. **编码格式**：输出文件使用UTF-8编码

## 🚀 下一步

1. 复制新编译的DLL到您的Tools目录
2. 运行您的导表命令
3. 检查 `csv_source` 目录是否生成了CSV文件
4. 使用Git查看CSV文件的变化：`git diff csv_source/`

## 💬 如果还有问题

如果按照上述步骤操作后仍然没有输出，请检查：

1. 是否成功复制了新的DLL文件
2. 控制台是否有错误或警告信息
3. 是否有数据文件被加载（检查日志中的 "load table" 相关信息）
4. 输出目录是否有写入权限

您可以将完整的控制台输出发给我，我会帮您进一步分析问题。

