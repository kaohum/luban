# 字段引用追踪功能 - 文件清单

## 已创建的文件

### 核心实现文件

#### 1. Luban.DataTarget.Builtin 项目
```
src/Luban.DataTarget.Builtin/FieldReference/
├── FieldReferenceCollection.cs          # 引用信息收集器
├── FieldReferenceCollectorVisitor.cs    # 数据访问器（遍历配置数据）
└── FieldReferenceDataTarget.cs          # DataTarget 实现（生成文本报告）
```

**功能说明：**
- `FieldReferenceCollection`: 收集和管理字段引用信息，提供统计和查询接口
- `FieldReferenceCollectorVisitor`: 实现 `IDataActionVisitor2` 接口，遍历配置数据识别引用
- `FieldReferenceDataTarget`: 实现 `DataTargetBase`，生成文本格式的引用报告

#### 2. Luban.CSharp 项目
```
src/Luban.CSharp/
├── CodeTarget/
│   └── CsharpFieldReferenceCodeTarget.cs    # CodeTarget 实现（生成C#代码）
└── Templates/
    └── cs-field-reference/
        └── field-reference.sbn              # Scriban 模板
```

**功能说明：**
- `CsharpFieldReferenceCodeTarget`: 实现 `CsharpCodeTargetBase`，生成 C# 查询代码
- `field-reference.sbn`: Scriban 模板，定义生成代码的格式

### 文档文件

```
docs/
├── FIELD_REFERENCE_QUICKSTART.md        # 快速开始指南（5分钟上手）
├── FIELD_REFERENCE_USAGE.md             # 详细使用说明
├── FIELD_REFERENCE_EXAMPLES.md          # 示例代码和场景
└── FIELD_REFERENCE_IMPLEMENTATION.md    # 实现细节和架构说明
```

## 使用方式

### 方式一：生成文本报告

```bash
dotnet Luban.dll \
  --conf luban.conf \
  --dataTarget field-reference \
  -x field-reference.targetTable=TbItem \
  -x field-reference.targetField=Id
```

**输出：** `field_reference_TbItem_Id.txt`

### 方式二：生成 C# 代码

```bash
dotnet Luban.dll \
  --conf luban.conf \
  --codeTarget cs-field-reference \
  -x cs-field-reference.targetTable=TbItem \
  -x cs-field-reference.targetField=Id
```

**输出：** `FieldReference_TbItem_Id.cs`

## 核心特性

1. **字段引用分析**
   - 遍历所有配置表
   - 识别 `ref` 标签标记的引用关系
   - 支持嵌套结构（Bean、List、Array、Map）

2. **文本报告生成**
   - 人类可读的格式
   - 包含统计信息
   - 显示完整的字段路径

3. **代码生成**
   - 生成静态查询类
   - 提供多种查询方法
   - 支持运行时使用

4. **灵活配置**
   - 自定义输出文件名
   - 支持命令行参数
   - 可集成到构建流程

## 技术架构

### 设计模式
- **访问者模式**：遍历配置数据
- **模板方法模式**：DataTarget 和 CodeTarget 基类
- **策略模式**：不同的输出策略（文本/代码）

### 核心组件
1. **Collection**：收集引用信息
2. **Visitor**：遍历配置数据
3. **Context**：维护遍历上下文
4. **Target**：生成输出

### 参考实现
本实现参考了 Luban.L10N 中的 `text-list` 功能，采用了相似的架构和模式。

## 扩展性

### 支持的数据类型
- 基础类型：int, long, string, float, double, bool, byte, short
- 时间类型：datetime, day, hour, minute, second, millisecond
- 枚举类型：enum
- 复杂类型：bean, array, list, set, map

### 可扩展点
1. **新的输出格式**：添加新的 DataTarget（如 JSON、XML）
2. **新的语言支持**：添加新的 CodeTarget（如 TypeScript、Java）
3. **新的分析功能**：扩展 Visitor 实现更多分析
4. **可视化支持**：生成依赖图（GraphViz）

## 性能特点

- **时间复杂度**：O(表数量 × 记录数 × 字段数)
- **空间复杂度**：O(引用数量)
- **适用规模**：中小型配置表（< 10000 条记录）
- **优化建议**：并行处理、缓存类型信息、增量分析

## 使用场景

1. **配置重构**：了解修改影响范围
2. **依赖分析**：分析配置表依赖关系
3. **运行时验证**：验证配置完整性
4. **文档生成**：自动生成依赖文档
5. **CI/CD 集成**：自动化配置分析

## 下一步

### 建议的改进
1. 支持反向查询（查询某表引用了哪些表）
2. 循环依赖检测
3. 依赖图可视化
4. 性能优化（并行处理、缓存）
5. 更多语言支持（TypeScript、Java、Python）

### 测试建议
1. 单元测试：测试各个组件
2. 集成测试：测试完整流程
3. 性能测试：测试大型配置表
4. 边界测试：测试特殊情况

## 相关文档

- [快速开始](FIELD_REFERENCE_QUICKSTART.md) - 5分钟快速上手
- [使用说明](FIELD_REFERENCE_USAGE.md) - 详细的使用文档
- [示例代码](FIELD_REFERENCE_EXAMPLES.md) - 丰富的示例
- [实现细节](FIELD_REFERENCE_IMPLEMENTATION.md) - 架构和设计

## 总结

本次实现为 Luban 导表系统添加了完整的字段引用追踪功能，包括：

✅ 核心功能实现（3个类）
✅ 代码生成支持（CodeTarget + 模板）
✅ 完整的文档（4个文档文件）
✅ 灵活的配置选项
✅ 良好的扩展性

该功能可以帮助开发者更好地理解和管理配置表之间的依赖关系，提高配置管理的效率和安全性。

