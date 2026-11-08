using System;
using System.Collections.Generic;
using System.Data.SQLite;
using MemberManager.Common;
using MemberManager.DAL;
using MemberManager.Models;

namespace MemberManager.BLL
{
    /// <summary>登录与密码（开发文档 §6.1）。密码 SHA-256+固定盐。</summary>
    public class AuthService
    {
        /// <summary>
        /// 登录下拉框用的账号列表（姓名按角色排序）。
        /// 只返回「启用 + 已设密码 + 未删除」的人：在员工管理里登记但没开登录权限的技师不该出现在这里。
        /// </summary>
        public List<Staff> ListEnabledStaff()
        {
            var list = new List<Staff>();
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // 11-04 店主：「员工管理」的删除只是从员工名单移除（不做项目），
                // 不该连带把登录也废掉 —— 所以这里不再按 deleted_at 过滤，
                // 只要 enabled=1 且有密码就能登录、就该出现在下拉里。
                cmd.CommandText = "SELECT id,name,IFNULL(login_name,''),role,phone,pwd_hash,enabled FROM staff" +
                    // 店主 10-24：登录下拉只列「能登录的角色」——技师一律不出现。
                    // 光靠「不给密码」不够：老库可能残留技师密码，会漏进这个清单。
                    " WHERE enabled=1 AND IFNULL(pwd_hash,'')<>''" +
                    " AND role<>'技师'" +
                    " ORDER BY CASE role WHEN '店长' THEN 0 WHEN '收银员' THEN 1 ELSE 2 END, id";
                using (var r = cmd.ExecuteReader())
                {
                    while (r.Read())
                    {
                        list.Add(new Staff
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            LoginName = r.GetString(2),
                            Role = r.GetString(3),
                            Phone = r.IsDBNull(4) ? null : r.GetString(4),
                            PwdHash = r.GetString(5),
                            Enabled = r.GetInt32(6) == 1
                        });
                    }
                }
            }
            return list;
        }

        /// <summary>登录。失败抛中文业务异常（密码错误/账号已停用）。</summary>
        public void Login(string name, string pwd)
        {
            if (string.IsNullOrWhiteSpace(name)) throw new BizException(UiText.LoginErrorEmpty);
            if (string.IsNullOrEmpty(pwd)) throw new BizException(UiText.LoginErrorEmpty);

            Staff staff = null;
            using (var conn = Db.OpenConnection())
            using (var cmd = conn.CreateCommand())
            {
                // 11-04 店主：登录改按唯一「登录名」查。
                // 旧实现 WHERE name=@n 只取第一条 —— 店里两个「李」时永远命中停用的那个，
                // 新加的账号根本进不去。现在 login_name 唯一，谁输的就是谁。
                // 找不到再按姓名兜底一次（老习惯输真名仍能登），但只取启用中的，
                // 兜不住就报密码错误，绝不静默登成别人。
                cmd.CommandText = "SELECT id,name,role,pwd_hash,enabled FROM staff WHERE login_name=@n";
                var p = cmd.CreateParameter(); p.ParameterName = "@n"; p.Value = name; cmd.Parameters.Add(p);
                using (var r = cmd.ExecuteReader())
                {
                    if (r.Read())
                    {
                        staff = new Staff
                        {
                            Id = r.GetInt32(0),
                            Name = r.GetString(1),
                            Role = r.GetString(2),
                            PwdHash = r.GetString(3),
                            Enabled = r.GetInt32(4) == 1
                        };
                    }
                }
                if (staff == null)
                {
                    cmd.CommandText = "SELECT id,name,role,pwd_hash,enabled FROM staff" +
                        " WHERE name=@n AND enabled=1 ORDER BY id LIMIT 1";
                    using (var r = cmd.ExecuteReader())
                    {
                        if (r.Read())
                        {
                            staff = new Staff
                            {
                                Id = r.GetInt32(0),
                                Name = r.GetString(1),
                                Role = r.GetString(2),
                                PwdHash = r.GetString(3),
                                Enabled = r.GetInt32(4) == 1
                            };
                        }
                    }
                }
            }

            if (staff != null && !staff.Enabled) throw new BizException(UiText.LoginErrorDisabled);
            // 店主 10-24 定：技师是「做项目的人」，不进系统登录。
            // 员工页登记技师只用于收银时选操作员、算提成；登录账号在「系统设置·登录账号」单独管。
            // 这里硬拦而不是只靠「不给密码」——万一老库/误操作给技师设了密码，也登不进来。
            if (staff != null && staff.Role == "技师")
                throw new BizException("技师不提供登录：技师只用于做项目与算提成，如需登录请改为收银员账号");
            string hash = DatabaseInitializer.HashPassword(pwd);
            if (staff == null || !string.Equals(staff.PwdHash, hash, StringComparison.OrdinalIgnoreCase))
                throw new BizException(UiText.LoginErrorWrongPwd);

            Session.SignIn(staff.Id, staff.Name, staff.Role);
            Logger.Info("登录成功：" + staff.Name + "（" + staff.Role + "）");
        }

        /// <summary>登录并返回当前员工信息（桥接用）。</summary>
        public SessionInfo LoginEx(string name, string pwd)
        {
            Login(name, pwd);
            return new SessionInfo { Id = Session.StaffId, Name = Session.Name, Role = Session.Role };
        }

        public class SessionInfo
        {
            public int Id;
            public string Name;
            public string Role;
        }

        /// <summary>
        /// 退出登录：清当前会话（前端切换账号也走这里，先登出再重新登录）。
        /// 不校验旧密码——前端已要求用户输入目标账号的密码才能切换。
        /// 写入操作日志留痕，谁在什么时候登出的可追溯（店长共享账号时尤其有用）。
        /// </summary>
        public void Logout()
        {
            string who = Session.IsLoggedIn ? Session.Name : "未登录";
            // 先取 who 再登出：登出后 Session.Name 已清空，取不到
            Session.SignOut();
            Logger.Info("退出登录：" + who);
            // 操作日志留痕（不走事务版 W.Log——那里要一个活连接+事务，这里没必要开事务）
            try
            {
                using (var conn = Db.OpenConnection())
                using (var cmd = conn.CreateCommand())
                {
                    cmd.CommandText = "INSERT INTO op_logs(operator,module,action,detail,result) VALUES(@o,@m,@a,@d,'成功')";
                    var po = cmd.CreateParameter(); po.ParameterName = "@o"; po.Value = who; cmd.Parameters.Add(po);
                    var pm = cmd.CreateParameter(); pm.ParameterName = "@m"; pm.Value = "权限与日志"; cmd.Parameters.Add(pm);
                    var pa = cmd.CreateParameter(); pa.ParameterName = "@a"; pa.Value = "退出登录"; cmd.Parameters.Add(pa);
                    var pd = cmd.CreateParameter(); pd.ParameterName = "@d"; pd.Value = who + " 退出账号"; cmd.Parameters.Add(pd);
                    cmd.ExecuteNonQuery();
                }
            }
            catch (Exception ex) { Logger.Warn("退出登录日志写入失败：" + ex.Message); }
        }

    }
}
