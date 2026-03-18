# CSV源文件导出功能 - 快速开始

## 编译成功 ✓

项目已成功编译，没有错误！

## 使用方法

### 方法1：通过配置文件

在你的Luban配置文件（如`luban.conf`）中添加配置：

```json
{
  "targets": [
    {
      "name": "your_target",
      "options": {
        "csvSourceOutputDir": "./csv_source"
      }
    }
  ]
}
```

然后正常运行导表命令：

```bash
dotnet Luban.dll -t your_target -c cs-bin -d bin --conf luban.conf
```

### 方法2：通过命令行参数（推荐用于测试）

直接在命令行中指定CSV输出目录：

```bash
dotnet Luban.dll -t your_target -c cs-bin -d bin \
  -x csvSourceOutputDir=./csv_source \
  --conf luban.conf
```

### 方法3：仅导出CSV源文件（不生成代码和数据）

如果只想生成CSV源文件用于版本对比：

```bash
dotnet Luban.dll -t your_target \
  -x csvSourceOutputDir=./csv_source \
  -f \
  --conf luban.conf
```

参数说明：
- `-f` 或 `--forceLoadTableDatas`：强制加载数据（即使没有指定代码和数据目标）
- `-x csvSourceOutputDir=路径`：指定CSV源文件输出目录

## 输出示例

假设你有一个Excel文件 `item.xlsx`，包含以下数据：

| ##  | id | name | price |
|-----|----|------|-------|
| ##type | int | string | float |
| ##desc | 物品ID | 物品名称 | 价格 |
| | 1 | 剑 | 100.5 |
| | 2 | 盾 | 80.0 |

导出后会生成 `csv_source/item.csv`：

```csv
## Sheet: Sheet1
id,name,price
##type,int,string,float
##desc,物品ID,物品名称,价格
1,剑,100.5
2,盾,80.0
```

## 主要优势

1. **便于版本控制**：CSV格式在Git中可以清晰看到每行数据的变化
2. **方便对比**：使用任何文本对比工具都能轻松对比数据差异
3. **易于合并**：在分支合并时，CSV格式的冲突更容易解决
4. **无侵入性**：不影响原有导表流程，可选启用

## 实际应用场景

### 场景1：数据审查
```bash
# 导出CSV源文件
dotnet Luban.dll -t server -x csvSourceOutputDir=./review -f --conf luban.conf

# 使用Git查看变化
git diff review/
```

### 场景2：分支合并
```bash
# 在feature分支
git add csv_source/
git commit -m "Update item data"

# 合并到main分支时，CSV格式更容易解决冲突
git merge feature-branch
```

### 场景3：持续集成
```bash
# 在CI/CD流程中自动生成CSV源文件
dotnet Luban.dll -t server -x csvSourceOutputDir=./artifacts/csv -f --conf luban.conf

# 将CSV文件作为构建产物保存
```

## 配置选项

| 选项名 | 类型 | 默认值 | 说明 |
|--------|------|--------|------|
| csvSourceOutputDir | string | 空 | CSV源文件输出目录，为空则不导出 |

## 注意事项

1. **首次使用**：建议先在测试环境验证输出结果
2. **目录权限**：确保输出目录有写入权限
3. **编码格式**：输出文件使用UTF-8编码
4. **特殊字符**：包含逗号、引号、换行的字段会自动转义

## 完整示例

假设你的项目结构如下：

```
MyGame/
├── Config/
│   ├── luban.conf
│   ├── Defines/
│   │   └── __tables__.xlsx
│   └── Datas/
│       ├── item.xlsx
│       └── skill.xlsx
```

**luban.conf 配置：**

```json
{
  "schemaFiles": [
    {
      "fileName": "Defines/__tables__.xlsx",
      "type": "excel"
    }
  ],
  "targets": [
    {
      "name": "server",
      "options": {
        "inputDataDir": "./Datas",
        "outputCodeDir": "./Gen/Server/Code",
        "outputDataDir": "./Gen/Server/Data",
        "csvSourceOutputDir": "./Gen/CsvSource"
      }
    }
  ]
}
```

**运行命令：**

```bash
cd Config
dotnet path/to/Luban.dll -t server -c cs-bin -d bin --conf luban.conf
```

**输出结果：**

```
MyGame/
├── Config/
│   └── Gen/
│       ├── Server/
│       │   ├── Code/      # 生成的C#代码
│       │   └── Data/      # 生成的二进制数据
│       └── CsvSource/     # CSV源文件（新增）
│           ├── item.csv
│           └── skill.csv
```

## 下一步

1. 尝试运行一次导表，查看生成的CSV文件
2. 将CSV文件添加到Git版本控制
3. 修改Excel数据后再次导表，使用`git diff`查看变化
4. 体验在分支合并时CSV格式带来的便利

## 技术支持

详细文档请参考：`CSV_SOURCE_USAGE.md`

如有问题，请随时反馈！

