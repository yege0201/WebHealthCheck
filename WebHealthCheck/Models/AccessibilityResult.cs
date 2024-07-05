using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace WebHealthCheck.Models
{
    public class AccessibilityResult
    {
        // 序号
        public int Id { get; set; }

        // 目标
        public required string Target { get; set; }

        // URL
        public required string Url { get; set; }


        // 访问状态描述
        public required string AccessStateDesc { get; set; }

        // 网页标题
        public required string WebTitle { get; set; }

        // 网页内容
        public required string WebContent { get; set; }
    }
}
