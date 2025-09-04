添加后处理，支持llm模型调用VPet状态，项目为https://github.com/LorisYounger/VPet     软件结构
VPet-Simulator.Windows: 适用于桌面端的虚拟桌宠模拟器
Function 功能性代码存放位置

CoreMOD Mod管理类
MWController 窗体控制器
*WinDesign 窗口和UI设计

winBetterBuy 更好买窗口
winCGPTSetting ChatGPT 设置
winSetting 软件设置/MOD 窗口
winConsole 开发控制台
winGameSetting 游戏设置
winReport 反馈中心
MainWindows 主窗体,存放和展示Core

PetHelper 快速切换小标

VPet-Simulator.Tool: 方便制作MOD的工具(eg:图片帧生成)
VPet-Simulator.Core: 软件核心 方便内置到任何WPF应用程序(例如:VUP-Simulator)
Handle 接口与控件
IController 窗体控制器 (调用相关功能和设置,例如移动到侧边等)
Function 通用功能
GameCore 游戏核心,包含各种数据等内容
GameSave 游戏存档
IFood 食物/物品接口
PetLoader 宠物图形加载器
Graph 图形渲染
IGraph 动画基本接口
GraphCore 动画显示核心
GraphHelper 动画帮助类
GraphInfo 动画信息
FoodAnimation 食物动画 支持显示前中后3层夹心动画 不一定只用于食物,只是叫这个名字
PNGAnimation 桌宠动态动画组件
Picture 桌宠静态动画组件
Display 显示
basestyle/Theme 基本风格主题
Main.xaml 核心显示部件
MainDisplay 核心显示方法
MainLogic 核心显示逻辑
ToolBar 点击人物时候的工具栏
MessageBar 人物说话时候的说话栏
WorkTimer 工作时钟           其它数据
Hakoyu edited this page on Oct 11, 2023 · 11 revisions
宠物状态
Happy: 开心
Nomal: 普通
PoorCondition: 状态不佳
Ill: 生病
日期区间
Morning: 早晨
Afternoon: 下午
Night: 晚上
Midnight: 午夜
行动状态
Nomal: 普通
Work: 工作/学习
Sleep: 睡觉
Travel: 旅游
Empty: 空
食物类型
Food: 食物
Star: 收藏
Meal: 正餐
Snack: 零食
Drink: 饮料
Functional: 功能性
Drug: 药品
Gift: 礼物
状态模式
H: 开心/普通
L: 低状态/生病
好感度需求
N: 无需求
S: 低需求
M: 中需求
L: 高需求
饥渴需求
L: 一般口渴/饥饿
M: 有点口渴/饥饿
S: 非常口渴/饥饿
工作类型
Work: 工作
Study: 学习
Play: 玩耍
移动定位类型
None: 无
Left: 左
Right: 右
Top: 上
Bottom: 下
LeftGreater: 更大的左范围
RightGreater: 更大的右范围
TopGreater: 更大的上范围
BottomGreater: 更大的下范围
动画类型
Common: 通用
Raised_Dynamic: 被提起动态
Raised_Static: 被提起静态
Move: 移动
Default: 默认
Touch_Head: 触摸头部
Touch_Body: 触摸身体
Idel: 空闲
Sleep: 睡觉
Say: 说话
StateONE: 状态一
StateTWO: 状态二
StartUP: 宠物启动
Shutdown: 宠物关闭
Work: 工作
Switch_Up: 向上切换状态
Switch_Down: 向下切换状态
Switch_Thirsty: 切换口渴状态
Switch_Hunger: 切换饥饿状态
动画动作类型
Single: 单一动画
A_Start: 进入动画
B_Loop: 循环动画
C_End: 结束动画
Pages 12
Home

zh-Hans
简体中文主页面
其它数据
创建新模组
食物
点击文本
低状态文本
选择文本
皮肤(宠物)
工作
移动
Clone this wiki locally
https://github.com/LorisYounger/VPet.ModMaker.wiki.git
Footer
