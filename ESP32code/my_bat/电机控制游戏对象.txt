#include <SimpleFOC.h>

// 编码器设置
// 创建传感器对象，使用AS5600传感器，通信协议是I2C
MagneticSensorI2C sensor0 = MagneticSensorI2C(AS5600_I2C);
TwoWire I2Cone = TwoWire(0); // 使用I2C总线，0表示第0号I2C总线

// 创建电机对象，并设置电机0的极对数为7
BLDCMotor motor0 = BLDCMotor(7);
// 创建一个3路PWM驱动器对象，参数依次为U、V、W三相的PWM控制引脚，最后一个是使能引脚
BLDCDriver3PWM driver0 = BLDCDriver3PWM(32, 33, 25, 22);

void setup() {
    /*
     * begin()方法初始化I2C总线
     * 参数19：指定SDA（数据线）引脚连接到GPIO 19
     * 参数18：指定SCL（时钟线）引脚连接到GPIO 18
     * 参数400000UL：设置I2C通信频率为400kHz（高速模式）
     * 在setup()函数中调用，必须在使用I2C通信前完成初始化
     */
    I2Cone.begin(19, 18, 400000UL);

    // 初始化传感器硬件，指定使用的I2C总线对象
    sensor0.init(&I2Cone);

    // 将传感器与电机关联，使电机能够获取位置反馈信息
    motor0.linkSensor(&sensor0);

    // 设置电机0的电压
    driver0.voltage_power_supply = 12;

    // 初始化电机驱动器对象
    driver0.init();

    motor0.linkDriver(&driver0);
    motor0.foc_modulation = FOCModulationType::SpaceVectorPWM;

    // 初始化电机对象，必须在关联传感器和驱动器后调用
    motor0.init();
    // 初始化磁场定向控制算法，执行传感器校准和零点定位
    motor0.initFOC();

    // 启动串口, 115200为波特率
    Serial.begin(115200);
}


/**
 * 主循环函数 - 持续执行电机控制和角度监控
 *
 * 此函数在Arduino程序中重复运行，执行以下操作：
 * 1. 更新电机的磁场定向控制(FOC)状态
 * 2. 从传感器获取当前角度并转换为度数
 * 3. 通过串口输出角度值（保留两位小数）
 */
void loop() {

    // 执行电机的磁场定向控制(FOC)循环处理
    motor0.loopFOC();

    // 从传感器获取角度值（弧度制），并转换为度数
    float angle0_deg = sensor0.getAngle() * 180.0 / PI;

    // 将转换后的角度值通过串口打印出来，保留两位小数
    Serial.println(angle0_deg, 2);

}

