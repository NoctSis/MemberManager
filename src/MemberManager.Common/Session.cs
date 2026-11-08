namespace MemberManager.Common
{
    /// <summary>当前登录会话（操作员/角色）。登录成功后由 AuthService 填充。</summary>
    public static class Session
    {
        public static int StaffId { get; private set; }
        public static string Name { get; private set; }
        public static string Role { get; private set; }

        public static bool IsLoggedIn { get { return StaffId > 0; } }
        public static bool IsManager { get { return Role == "店长"; } }

        public static void SignIn(int staffId, string name, string role)
        {
            StaffId = staffId;
            Name = name;
            Role = role;
        }

        public static void SignOut()
        {
            StaffId = 0;
            Name = null;
            Role = null;
        }
    }
}
