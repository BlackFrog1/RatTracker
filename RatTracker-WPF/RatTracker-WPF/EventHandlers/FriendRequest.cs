﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace RatTracker_WPF.EventHandlers
{
    public class FriendRequestArgs : EventArgs
    {
        public string FriendName
        {
            get;
            set;
        }
    }
}
