# paramSchema 新增 array 类型支持

## 问题描述

**涉及文件**：`cloud-edge-web/src/components/common/yaml-config.vue`

**现象**：paramSchema 中 `type: "textarea"` 的字段，用户在表单中按 YAML 数组格式输入：

```
- device/sis/data
- sensors/batch
```

最终渲染到 `values.yaml` 时变成了字符串：

```yaml
Topics: "- device/sis/data\n- sensors/batch"
```

而不是期望的 YAML 列表：

```yaml
Topics:
  - "device/sis/data"
  - "sensors/batch"
```

**根因**：

1. `fmtYaml` 函数对所有非布尔、非数字的值一律加双引号包裹（`return "\"${String(v)}\""`），多行文本被当成了普通字符串
2. `getYamlValue` / `setYamlValue` 基于单行 `key: value` 正则匹配，无法解析 YAML 缩进块

## 需求

### 1. ParamField type 新增 `array` 类型

用于配置 YAML 列表字段（如 `Topics`、`args`、`command` 等）。

### 2. 前端表单渲染

`array` 类型字段使用**可增删行的列表编辑器**，用户逐条输入数组元素，而非用 textarea 手写 `- ` 前缀。

### 3. fmtYaml 支持数组序列化

将数组值输出为标准 YAML 列表块格式：

```yaml
key:
  - "value1"
  - "value2"
```

### 4. setYamlValue 支持数组块替换

替换对应 key 下的整个缩进块，而非目前的单行正则匹配。

### 5. getYamlValue 支持数组反序列化

读取已有 `values.yaml` 中的 YAML 列表块，回填到表单编辑器中。

## paramSchema 配置示例

```json
{
  "key": "topics",
  "label": "订阅主题列表",
  "type": "array",
  "defaultValue": ["device/sis/data"],
  "yamlPath": "appSettings.Mqtt.Topics",
  "description": "MQTT 订阅主题列表，支持多个，支持通配符 + 和 #",
  "span": 24
}
```

## 期望效果

用户在部署向导中选择 paramSchema 配置模式后，表单中展示列表编辑器，可增删主题行：

```
┌─────────────────────────────────────────┐
│ 订阅主题列表                             │
│ ┌─────────────────────────────────────┐ │
│ │ device/sis/data                  [×]│ │
│ │ sensors/batch                    [×]│ │
│ │ [+ 添加主题]                        │ │
│ └─────────────────────────────────────┘ │
└─────────────────────────────────────────┘
```

生成的 values.yaml 片段：

```yaml
Topics:
  - "device/sis/data"
  - "sensors/batch"
```
