using UnityEngine;
using UnityEngine.Serialization;
using System.IO.Ports;
using System;
using System.Collections.Generic;
using System.Threading;

public abstract class SerialController : MonoBehaviour
{
    /// <summary>
    /// 串口设置部分
    /// </summary>
    [Header("串口设置")]
    [SerializeField] private string portName = "COM3";
    [SerializeField] private int baudRate = 115200;

    /// <summary>
    /// 游戏对象部分
    /// </summary>
    [Header("游戏对象")]
    [SerializeField] protected Transform targetLink; // 玩家连杆对象（场景中名为 Link）
    [FormerlySerializedAs("envObstacle")]
    [SerializeField] protected Transform envObstacle; // 这个对象控制电机，模拟环境对玩家的作用（场景中名为 EnvObstacle）

    /// <summary>
    /// 串口通信相关变量
    /// </summary>
    protected SerialPort SerialPort; // 串口对象，用于串口通信
    protected Thread IoThread; // 单一线程处理读写
    private bool _isRunning = false; // 线程运行标志，控制IO线程是否继续执行

    /// <summary>
    /// 数据发送队列和锁
    /// </summary>
    protected readonly Queue<string> SendQueue = new Queue<string>();
    protected readonly object SendLock = new object();
    protected readonly object ReceiveLock = new object();

    /// <summary>
    /// 角度控制相关变量
    /// </summary>
    protected float CurrentAngle = 0f;
    protected float LastAngle = 0f;
    protected bool NewDataReceived = true;
    protected readonly object DataLock = new object();
    protected float ThreadSafeAngle = 0f;
    protected bool ThreadSafeDataFlag = false;

    protected bool isColliding = false;
    
    
    /// <summary>
    /// 初始化方法，该方法负责初始化目标对象（Link）和环境障碍物（EnvObstacle）的引用，并打开串口连接
    /// </summary>
    protected virtual void Start()
    {
        
        // 检查并设置目标对象，默认使用当前对象的变换组件
        if (targetLink == null)
            targetLink = this.transform;
        
        // 尝试查找并设置环境障碍物的引用（场景中原 Sphere 对象，现名为 EnvObstacle）
        if (envObstacle == null)
        {
            GameObject envObstacleObj = GameObject.Find("EnvObstacle");
            if (envObstacleObj != null)
            {
                envObstacle = envObstacleObj.transform;
            }
            else
            {
                Debug.LogWarning("场景中未找到名为 EnvObstacle 的对象，环境对象引用为空。");
            }
        }

        // 打开串口端口以进行后续通信
        OpenSerialPort();
        
        // 发送启动信号给ESP32，实现同步通信开启
        // 等待一小段时间确保串口完全打开后再发送启动命令
        Invoke("SendStartSignal", 0.5f);
    }


    protected virtual void Update()
    {
        if (this.NewDataReceived)
        {
            lock (DataLock)
            {
                this.CurrentAngle = this.ThreadSafeAngle;
                this.ThreadSafeDataFlag = false;
            }
            this.NewDataReceived = false;
        }
    }

    
    /// <summary>
    /// 打开串口连接
    /// </summary>
    protected void OpenSerialPort()
    {
        // 开始尝试打开串口连接
        try
        {
            // 获取所有可用的串口名称列表
            string[] availablePorts = SerialPort.GetPortNames();
            // 在调试控制台输出"可用串口:"提示信息
            Debug.Log("可用串口:");
            // 遍历所有可用串口并输出到调试控制台
            foreach (string port in availablePorts)
            {
                // 输出具体的可用串口名称
                Debug.Log(port);
            }

            // 创建串口实例，使用指定的端口名和波特率
            SerialPort = new SerialPort(portName, baudRate)
            {
                // 设置读取超时时间为50毫秒
                ReadTimeout = 5,
                // 设置写入超时时间为500毫秒
                WriteTimeout = 5,
                // 设置奇偶校验为无
                Parity = Parity.None,
                // 设置数据位为8位
                DataBits = 8,
                // 设置停止位为1位
                StopBits = StopBits.One
            };

            // 打开串口连接
            SerialPort.Open();
            // 设置IO线程的运行状态标志为true
            _isRunning = true;

            // 启动用于处理输入/输出的线程
            IoThread = new Thread(IOThreadFunction)
            {
                // 设置线程为后台线程
                IsBackground = true
            };
            // 启动IO线程
            IoThread.Start();

            // 输出串口已成功打开的信息，包括端口名和波特率
            Debug.Log($"串口已打开: {portName}, 波特率: {baudRate}");
        }
        // 捕获在串口操作过程中可能发生的异常
        catch (Exception ex)
        {
            // 记录串口打开失败的错误信息
            Debug.LogError($"打开串口失败: {ex.Message}");
            // 自动查找并尝试连接其他可用端口
            AutoFindAndConnect();
        }
    }
    
    
    /// <summary>
    /// 自动查找并连接可用串口
    /// </summary>
    private void AutoFindAndConnect()
    {
        string[] availablePorts = SerialPort.GetPortNames();
        if (availablePorts.Length > 0)
        {
            portName = availablePorts[0];
            Debug.Log($"尝试自动连接串口: {portName}");
            OpenSerialPort();
        }
        else
        {
            Debug.LogError("未找到可用串口！");
        }
    }

    
    // 定义一个处理输入/输出的线程函数
    protected virtual void IOThreadFunction()
    {
        // 当串口处于连接状态时持续循环
        while (IsConnected())
        {
            // // 打印isColliding状态
            // Debug.Log("isColliding: " + isColliding);

            // 开始尝试处理数据发送和接收
            try
            {
                // 1. 先处理发送
                // 声明一个字符串变量用于存储待发送的数据
                string dataToSend = null;
                // 对发送锁进行锁定，确保线程安全
                lock (SendLock) // 互斥锁
                {
                    // 检查发送队列中是否有数据
                    if (SendQueue.Count > 0)
                    {
                        Debug.Log($"发送队列中有数据，队列长度: {SendQueue.Count}");
                        // 从发送队列中取出一条数据
                        dataToSend = SendQueue.Dequeue(); // de-（表示"移除、离开"）+ queue（队列）
                        Debug.Log($"待发送数据: {dataToSend}");
                    }
                    // 如果有待发送的数据
                    if (dataToSend != null) // 仅在检测到碰撞时发送数据
                    {
                        // 通过串口发送数据（添加换行符）
                        SerialPort.WriteLine(dataToSend);
                    }
                }
                
                try
                {
                    // 声明一个字符串变量用于存储接收到的数据
                    string data = "";
                    // 锁定串口锁以确保线程安全
                    lock (ReceiveLock)
                    {
                        // 从串口读取一行数据
                        data = SerialPort.ReadLine();
                    }
                    // 如果接收到的数据不为空或null
                    if (!string.IsNullOrEmpty(data))
                    {
                        // 处理接收到的数据（去除首尾空白字符）
                        ProcessReceivedData(data.Trim());
                    }
                }
                // 捕获超时异常
                catch (TimeoutException)
                {
                    // 读取超时，继续循环
                }
                // 捕获其他可能的异常
                catch (Exception ex)
                {
                    // 记录串口读取错误信息
                    Debug.LogError($"串口读取错误: {ex.Message}");
                }

                // 3. 如果队列为空且无数据可读，短暂休眠
                // 如果没有数据需要发送
                if (dataToSend == null)
                {
                    // 休眠1毫秒，让出CPU资源给其他线程，同时保持响应性
                    // Thread.Sleep(1); // 很短的休眠，提高响应性
                }
            }
            // 捕获IO线程中的异常
            catch (Exception ex)
            {
                // 记录IO线程错误信息
                Debug.LogError($"IO线程错误: {ex.Message}");
                // 休眠10毫秒，避免异常导致的频繁重试
                Thread.Sleep(10);
            }
        }
    }

    
    /// <summary>
    /// 处理接收到的数据（抽象方法，子类实现）
    /// </summary>
    protected abstract void ProcessReceivedData(string data);

    
    /// <summary>
    /// 发送数据到串口
    /// </summary>
    public void SendData(string data)
    {
        lock (SendLock)
        {
            SendQueue.Enqueue(data); // 将数据添加到发送队列
        }
    }

    
    /// <summary>
    /// 检查串口是否连接
    /// </summary>
    public bool IsConnected()
    {
        return SerialPort != null && SerialPort.IsOpen && _isRunning;
    }

    
    /// <summary>
    /// 程序退出时清理资源
    /// </summary>
    private void OnDestroy()
    {
        CloseSerialPort();
    }

    
    private void OnApplicationQuit()
    {
        CloseSerialPort();
    }

    
    /// <summary>
    /// 关闭串口连接
    /// </summary>
    private void CloseSerialPort()
    {
        _isRunning = false;

        // 等待IO线程结束
        if (IoThread != null && IoThread.IsAlive)
        {
            IoThread.Join(500);
            if (IoThread.IsAlive)
            {
                IoThread.Abort();
            }
        }

        if (SerialPort != null)
        {
            if (SerialPort.IsOpen)
            {
                SerialPort.Close();
            }
            SerialPort.Dispose();
            SerialPort = null;
        }

        Debug.Log("串口已关闭");
    }
    
    /// <summary>
    /// 发送启动信号给ESP32
    /// </summary>
    private void SendStartSignal()
    {
        if (IsConnected())
        {
            // 发送'S'命令启动通信（与ESP32端的握手协议对应）
            SendData("S");
            Debug.Log("已发送启动信号给ESP32");
        }
    }
}
