export const fieldLabels={endpoint:'OPC UA 服务地址',host:'PLC 地址',port:'端口',username:'用户名',password:'密码',active_count_node_id:'有效报警数量节点',alarm_code_list_node_id:'报警码数组节点',publishing_interval_ms:'发布间隔（毫秒）',sampling_interval_ms:'采样间隔（毫秒）',max_alarm_count:'最大有效报警数量',inactive_code:'忽略的报警码',event_node_id:'报警事件节点',event_queue_size:'事件队列容量',rack:'机架编号',slot:'插槽编号',db_number:'DB 编号',start_byte:'起始字节地址',bit_number:'位编号',unit_id:'设备站号',register_address:'寄存器地址',data_type:'数据类型',topic:'订阅主题',client_id:'客户端标识',broker:'MQTT 服务地址',baudrate:'串口波特率',device:'串口设备',timeout:'连接超时',security_policy:'安全策略',security_mode:'安全模式'};
export function parseField(text,original){
 if(typeof original==='boolean'){if(!['true','false'].includes(text))throw Error('布尔参数只能填写 true 或 false');return text==='true';}
 if(typeof original==='number'){if(text.trim()===''||!Number.isFinite(Number(text)))throw Error('请输入有效数字');return Number(text);}
 if(original!==null&&typeof original==='object'){try{return JSON.parse(text);}catch{throw Error('扩展配置需要有效 JSON');}}
 return text;
}
