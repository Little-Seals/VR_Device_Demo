/**
将传感器采集的角度数据上传到串口，并接收串口位置控制指令
*/

#include <SimpleFOC.h>  // 包含SimpleFOC库，用于无刷直流电机磁场定向控制

// 创建I2C磁传感器对象，使用AS5600芯片
MagneticSensorI2C sensor = MagneticSensorI2C(AS5600_I2C);
// 创建第二个I2C总线对象，使用I2C0接口
TwoWire I2Cone = TwoWire(0);

// 电机参数
// 创建BLDC电机对象，极对数为7
BLDCMotor motor = BLDCMotor(7);
// 创建3相PWM驱动器对象，引脚分别为32,33,25，使能引脚为12
BLDCDriver3PWM driver = BLDCDriver3PWM(32,33,25,12);

// 命令设置
float target_angle = motor.shaft_angle;  // 目标角度变量



// 握手协议标志
bool communicationStarted = false;  // 通信是否已启动的标志

// 创建串口通信命令解析器对象
Commander command = Commander(Serial);
// 定义启动通信命令的处理函数
void doStartCommunication(char* cmd)
{
    communicationStarted = true;
    Serial.println("COMMUNICATION_STARTED");
}

// 定义目标角度设置命令的处理函数
void doTarget(char* cmd)
{
    // 只有在通信启动后才处理目标角度命令
    if (communicationStarted) {
        command.scalar(&target_angle, cmd);
    }
}



// 握手超时设置（毫秒）
const unsigned long HANDSHAKE_TIMEOUT_MS = 5000;  // 5秒超时
unsigned long handshakeStartTime = 0;


// 函数声明
void I2C_init();  // 声明I2C初始化函数

void setup() {
    I2C_init();  // 调用I2C初始化函数

    // 供电电压设置 [V]
    driver.voltage_power_supply = 12;  // 设置驱动器供电电压为12V
    driver.init();  // 初始化驱动器硬件

    // 连接电机和driver对象
    motor.linkDriver(&driver);  // 将电机控制器与驱动器关联

    // FOC模型选择
    motor.foc_modulation = FOCModulationType::SpaceVectorPWM;  // 使用空间矢量PWM调制

    // 运动控制模式设置 - 位置控制
    motor.controller = MotionControlType::torque;

    // 速度PI环设置
    motor.PID_velocity.P = 0.021;  // 速度环比例增益
    motor.PID_velocity.I = 0.12;   // 速度环积分增益

    // 角度P环设置
    motor.P_angle.P = 20;  // 角度环比例增益

    // 电机电压限制
    motor.voltage_limit = 12;  // 限制电机最大工作电压为12V

    // 速度低通滤波时间常数
    motor.LPF_velocity.Tf = 0.01;  // 速度滤波器时间常数，用于平滑速度测量

    // 设置最大速度限制
    motor.velocity_limit = 20;  // 限制电机最大转速为20rad/s

    Serial.begin(115200);  // 初始化串口通信，波特率115200
    motor.useMonitoring(Serial);  // 启用电机监控功能，通过串口输出调试信息

    // 初始化电机
    motor.init();  // 电机基础初始化

    // 初始化 FOC
    motor.initFOC();  // 初始化磁场定向控制算法

    command.add('T', doTarget, "target_angle");  // 注册串口命令：'T'命令用于设置目标角度

    // 添加握手命令 'S' - Start communication
    command.add('S', doStartCommunication, "start_communication");

    // 记录握手开始时间
    handshakeStartTime = millis();

    // 发送等待握手信号
    Serial.println("WAITING_FOR_START");
}


void loop() {

    motor.loopFOC();
    command.run();
    // 检查握手超时
    if (!communicationStarted) {
        unsigned long currentMillis = millis();
        if (currentMillis - handshakeStartTime > HANDSHAKE_TIMEOUT_MS) {
            // 超时后自动启动通信（兼容性处理）
            communicationStarted = true;
            Serial.println("COMMUNICATION_STARTED_TIMEOUT");
        }
        return;  // 等待期间不执行其他操作
    }




    float curAngle = motor.shaft_angle;
    motor.move(4 * (target_angle - curAngle) +
    0.00  * motor.shaft_velocity);

    // 从传感器获取角度值（弧度制），并转换为度数
    float angle0_deg = sensor.getAngle() * 180.0 / PI;

    // 将转换后的角度值通过串口打印出来，保留两位小数
    Serial.println(angle0_deg, 2);

}


// I2C初始化函数，磁编码器使用I2C总线和 MCU通信
void I2C_init(){
    // 设置I2C引脚为上拉输入模式（配置引脚状态）
    pinMode(32, INPUT_PULLUP);
    pinMode(33, INPUT_PULLUP);
    pinMode(25, INPUT_PULLUP);

    // 启动I2C通信，SDA=19, SCL=18, 频率400kHz
    I2Cone.begin(19,18, 400000UL);

    // 使用指定的I2C总线初始化磁传感器
    sensor.init(&I2Cone);

    // 连接motor对象与传感器对象，使电机能够读取传感器数据
    motor.linkSensor(&sensor);
}