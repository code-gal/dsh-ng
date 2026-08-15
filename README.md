# DeepSeek Harness (DSH) DIY 生态大仓库 (Monorepo)

欢迎来到我的 DeepSeek Harness 私人 DIY 大仓。

DeepSeek Harness (dsh) 是一个一切皆插件、鼓励高度折腾的开源智能体引擎。本项目集合了我对其衍生出的扩展工具、桌面客户端代理、独立插件及自用规范指南。

## 仓库结构分布
本仓库采用 Monorepo（单一源码库多项目）的管理结构：

- [`dsh-ng-desktop/`](dsh-ng-desktop/README.md) —— dsh 底层引擎的轻量级跨平台桌面代理壳（基于 .NET AOT），收敛了后台执行、端口管理与自焚式干净卸载能力。
- *待扩充区域（例如各类 Plugins、Widgets 等）*

## 开发范式与规范
全仓要求无论是人类工程师参与还是 AI Agent 进行续写演进，均严格奉行 **Spec Coding** 原则。
详细的文书管理规则见全体开发者必读的：[全局文档协作标准 (specs/0_DOC_STANDARDS.md)](specs/0_DOC_STANDARDS.md)。
关于新想法、待验证功能的草案池见：[生态系统规划与点子库 (specs/IDEAS.md)](specs/IDEAS.md)。

## 工具与生态
所有子项目相互独立但统一遵循根目录 `.copilot-instructions.md` 中指派的 AI 协同基律。