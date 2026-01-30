# CSV 数据导出器

## 功能说明

CSV 数据导出器可以将 Luban 配置表导出为 CSV 格式文件，方便与 Excel、数据库等工具进行数据交换。

## 使用方法

在 Luban 命令行中指定数据导出格式为 `csv`：

```bash
dotnet Luban.dll -t csv -d <output_directory> ...其他参数
```

## 配置选项

CSV 导出器支持以下配置选项：

### 1. 分隔符 (delimiter)

设置 CSV 文件的字段分隔符，默认为逗号 `,`。

```bash
--csv:delimiter ";"
```

示例：使用分号作为分隔符
```bash
dotnet Luban.dll -t csv --csv:delimiter ";" -d ./output
```

### 2. 表头 (header)

控制是否在 CSV 文件第一行输出字段名作为表头，默认为 `true`。

```bash
--csv:header false
```

示例：不输出表头
```bash
dotnet Luban.dll -t csv --csv:header false -d ./output
```

### 3. 文件编码 (file_encoding)

设置 CSV 文件的字符编码，默认为 UTF-8。

```bash
--csv:file_encoding "gbk"
```

示例：使用 GBK 编码
```bash
dotnet Luban.dll -t csv --csv:file_encoding "gbk" -d ./output
```

### 4. 输出文件扩展名 (output_data_extension)

自定义输出文件的扩展名，默认为 `.csv`。

```bash
--csv:output_data_extension "txt"
```

## 数据格式说明

### 基本类型

基本数据类型（int、float、string、bool 等）直接输出为对应的文本值。

### 复杂类型

对于复杂类型（bean、array、list、map 等），会转换为紧凑的文本格式：

- **Bean（结构体）**：`{field1:value1;field2:value2}`
- **Array/List（数组/列表）**：`[value1;value2;value3]`
- **Map（字典）**：`{key1:value1;key2:value2}`

### 特殊字符处理

CSV 导出器会自动处理特殊字符：
- 包含逗号、引号、换行符的字段会自动用双引号包裹
- 字段内的双引号会被转义为两个双引号 `""`

## 示例

假设有如下配置表定义：

```xml
<bean name="Item">
    <var name="id" type="int"/>
    <var name="name" type="string"/>
    <var name="price" type="int"/>
</bean>

<table name="TbItem" value="Item" index="id"/>
```

导出的 CSV 文件内容：

```csv
id,name,price
1,苹果,10
2,香蕉,8
3,"特殊物品,含逗号",15
```

## 完整命令示例

```bash
# 基本用法
dotnet Luban.dll \
  -t csv \
  -d ./output/csv \
  --conf ./luban.conf

# 使用分号分隔符和 GBK 编码
dotnet Luban.dll \
  -t csv \
  --csv:delimiter ";" \
  --csv:file_encoding "gbk" \
  -d ./output/csv \
  --conf ./luban.conf

# 不输出表头
dotnet Luban.dll \
  -t csv \
  --csv:header false \
  -d ./output/csv \
  --conf ./luban.conf
```

## 注意事项

1. CSV 格式适合简单的表格数据，对于包含大量嵌套结构的复杂配置，建议使用 JSON 或 XML 格式。
2. 复杂类型（如嵌套的 bean、数组等）会被序列化为紧凑的文本格式，可能不便于直接在 Excel 中编辑。
3. 导出的 CSV 文件默认使用 UTF-8 编码，如需在中文 Excel 中正常显示，可能需要指定 GBK 编码。
