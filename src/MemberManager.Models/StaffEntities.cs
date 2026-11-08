using System;

namespace MemberManager.Models
{
    /// <summary>员工账号（staff 表）。role：店长/收银员/技师（变更单 #1 扩技师）。</summary>
    public class Staff
    {
        public int Id { get; set; }                    // id
        public string Name { get; set; }               // name（真名，可重复）
        public string LoginName { get; set; }          // login_name（登录名，唯一；11-04 加）
        public string Role { get; set; }               // role
        public string Phone { get; set; }              // phone
        public string PwdHash { get; set; }            // pwd_hash（SHA-256+固定盐，十六进制小写）
        public bool Enabled { get; set; }              // enabled
    }

    /// <summary>角色权限矩阵行（role_perms 表）。</summary>
    public class RolePerm
    {
        public string Role { get; set; }               // role
        public string Module { get; set; }             // module（10 个功能模块之一）
        public bool Allowed { get; set; }              // allowed
    }

    /// <summary>操作日志（op_logs 表，触发器保护：只增不删；被拒绝的操作也记录）。</summary>
    public class OpLog
    {
        public long Id { get; set; }                   // id
        public string OperatorName { get; set; }       // operator
        public string Module { get; set; }             // module
        public string Action { get; set; }             // action
        public string Detail { get; set; }             // detail
        public string Result { get; set; }             // result：成功/拒绝
        public DateTime CreatedAt { get; set; }        // created_at
    }

    /// <summary>储值方案（recharge_packages 表）。金额档位唯一。</summary>
    public class RechargePackage
    {
        public int Id { get; set; }                    // id
        public decimal Amount { get; set; }            // amount >0
        public decimal Gift { get; set; }              // gift 赠送金额 >=0
        public bool Enabled { get; set; }              // enabled
    }
}
