﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Web;

namespace TiTaTo.Data.Models
{
    public class ConnectionInfo
    {
        public Guid UserID { get; set; }
        public Guid ChatRoomID { get; set; }
    }
}