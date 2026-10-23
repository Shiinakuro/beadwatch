拼豆先锋诊断日志

适用：本地混元3D 报 CUDA 错误、无法调用显卡、或其他本地 AI 问题。

使用方法
1. 保持 collect_diagnostics.ps1 和 生成诊断日志.cmd 在同一个文件夹。
2. 双击“生成诊断日志.cmd”。
3. 桌面会生成：拼豆先锋_诊断日志_日期_时间.zip。
4. 把这个 zip 发给开发者。

会收集
- 显卡型号、驱动版本、CUDA 版本、计算能力
- 应用版本与安装目录
- 本地 Python、PyTorch、TorchVision 版本
- torch.cuda.get_arch_list() 与 CUDA 矩阵乘法自检（用来判断是否缺少 sm_120 内核）
- pip list / pip check
- 应用 userdata 下的 *.log
- 文件清单（只列名称和大小）

不会收集
- API Key、settings.json
- 工程文件、缓存图片、示例图片
- 模型权重文件

说明
只安装主程序、没有安装本地 AI 依赖时，脚本仍会收集显卡和系统信息，并提示找不到 Python 运行时。
脚本只读信息并生成 zip，不修改程序、不安装任何东西。
