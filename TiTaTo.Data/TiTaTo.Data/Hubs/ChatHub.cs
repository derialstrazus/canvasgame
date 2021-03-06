﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Web;
using Microsoft.AspNet.SignalR;
using TiTaTo.Data.DataAccess;
using TiTaTo.Data.Models;

namespace TiTaTo.Data.Hubs
{
    public class ChatHub : Hub
    {
        //TODO: Get rid of this
        public void Send(string name, string message)
        {
            Clients.All.broadcastMessage(name, message);
        }







        SingletonDB s1 = SingletonDB.Instance;

        private Guid ServerUserID
        {
            get
            {
                return s1.Users.First(x => x.Name == "Server").ID;
            }
        }

        public void JoinServer(string userIDString)
        {
            //Find the user
            User user = GetUserByID(userIDString);

            //Establish user
            user.LastOnline = DateTime.MaxValue;
            s1.ConnectionStrings.Add(Context.ConnectionId, new ConnectionInfo() { UserID = user.ID });

            //Help client render
            var chatRooms = s1.ChatRooms
                .Where(x => x.Users.Any(y => y.ID == user.ID) || x.IsPublic)
                .Select(c => new {
                    ID = c.ID,
                    RoomName = c.RoomName,
                    HasReadLatest = c.Users.Any(x => x.ID == user.ID) ? c.Users.First(x => x.ID == user.ID).HasReadLatest : false
                });
            IEnumerable<User> users = s1.Users;
            Clients.Caller.Initialize(new
            {
                chatRooms = chatRooms.ToList(),
                users = users.ToList()
            });

            //Notify all other users
            //TODO: Implement this on front
            Clients.Others.SomeoneJoined(user);
        }

        public void InviteToChat(string invitedUserIDString)
        {
            //add room to db
            //notify all users that are invited to this room

            var invitedUser = GetUserByID(invitedUserIDString);
            var currentUser = GetUserByConnectionID(Context.ConnectionId);

            var existingChatRoom = s1.ChatRooms
                .Where(x => x.Users.Count == 2)
                .FirstOrDefault(x => x.Users.Any(y => y.ID == invitedUser.ID) && x.Users.Any(y => y.ID == currentUser.ID));

            if (existingChatRoom != null)
            {
                Clients.Caller.RoomExists(existingChatRoom);
            }
            else
            {
                var newChatRoom = new ChatRoom()
                {
                    ID = Guid.NewGuid(),
                    Users = new List<User>()
                    {
                        currentUser,
                        invitedUser
                    },
                    Messages = new List<Message>(),
                    RoomName = currentUser.Name + ", " + invitedUser.Name,
                    IsPublic = false
                };

                s1.ChatRooms.Add(newChatRoom);

                //Notify users
                foreach (User user in newChatRoom.Users)
                {
                    var chatRooms = s1.ChatRooms
                        .Where(x => x.Users.Any(y => y.ID == user.ID) || x.IsPublic)
                        .Select(c => new {
                            ID = c.ID,
                            RoomName = c.RoomName,
                            HasReadLatest = c.Users.Any(x => x.ID == user.ID) ? c.Users.First(x => x.ID == user.ID).HasReadLatest : false
                        });
                    IEnumerable<User> users = s1.Users;
                    var sendThis = new { chatRooms = chatRooms.ToList(), users = users.ToList() };

                    var userCxIds = s1.ConnectionStrings.Where(x => x.Value.UserID == user.ID).Select(x => x.Key);
                    foreach (var cxId in userCxIds)
                    {
                        Clients.Client(cxId).Initialize(sendThis);
                        if (cxId != s1.ConnectionStrings.First(x => x.Value.UserID == currentUser.ID).Key)
                        {
                            Clients.Client(cxId).InvitationReceived(currentUser.Name, newChatRoom.ID);
                        }
                    }
                }

                s1.ConnectionStrings[Context.ConnectionId].ChatRoomID = newChatRoom.ID;
                Clients.Caller.RoomEntered(newChatRoom);
            }
        }

        public void EnterRoom(string roomIDString)
        {
            //Get the room
            ChatRoom chatRoom = GetChatRoom(roomIDString);

            //Get the user
            User user = GetUserByConnectionID(Context.ConnectionId);

            //Check if user is a member of this room.  
            //If not, check if room is public.
            //If yes, add him to the room, otherwise kick him out.
            if (chatRoom.Users.All(x => x.ID != user.ID))
            {
                if (!chatRoom.IsPublic)
                {
                    throw new HubException("User is not allowed in this room");
                }
                else
                {
                    chatRoom.Users.Add(user);
                    chatRoom.LastUpdated = DateTime.Now;
                }
            }

            

            //establish location of this connection id
            s1.ConnectionStrings[Context.ConnectionId].ChatRoomID = chatRoom.ID;

            //let client know room is entered
            Clients.Caller.RoomEntered(chatRoom);

            //if a user has entered the room, then he must have read the messages
            chatRoom.Users.First(x => x.ID == user.ID).HasReadLatest = true;

            //Notify all members
            var thisMessage = new Message()
            {
                Content = user.Name + " has joined the chat room",
                SenderID = ServerUserID,
                TimeStamp = DateTime.Now
            };
            var listeners = s1.ConnectionStrings.Where(x => x.Value.ChatRoomID == chatRoom.ID).Select(x => x.Key);
            foreach (var listener in listeners)
            {
                Clients.Client(listener).NewMessage(thisMessage);
            }
        }

        public void SendMessage(string message, string roomIDString)
        {
            ChatRoom chatRoom = GetChatRoom(roomIDString);
            User user = GetUserByConnectionID(Context.ConnectionId);
            if (chatRoom.Users.All(x => x.ID != user.ID))
            {
                throw new HubException("User is not allowed to talk in this chatroom");
            }

            //Add message to db
            var newMessage = new Message()
            {
                SenderID = user.ID,
                Content = message,
                TimeStamp = DateTime.Now
            };
            chatRoom.Messages.Add(newMessage);
            chatRoom.LastUpdated = newMessage.TimeStamp;            
                        
            //Notify all users who are currently in that room
            var currentListeners = s1.ConnectionStrings.Where(x => x.Value.ChatRoomID == chatRoom.ID);
            foreach (var listener in currentListeners)
            {
                GetUserByConnectionID(listener.Key).HasReadLatest = true;
                Clients.Client(listener.Key).CurrentRoomNewMessage(newMessage);
            }

            var currentListenerIds = currentListeners.Select(x => x.Value.UserID);
            var otherUsers = chatRoom.Users.Where(x => currentListenerIds.All(y => y != x.ID));

            //Notify all other members of that room
            foreach (var otheruser in otherUsers)
            {
                otheruser.HasReadLatest = false;                
                if (s1.ConnectionStrings.Any(x => x.Value.UserID == otheruser.ID))
                {
                    var otherUserCxID = s1.ConnectionStrings.FirstOrDefault(x => x.Value.UserID == otheruser.ID).Key;
                    Clients.Client(otherUserCxID).OtherRoomNewMessage(chatRoom.ID);
                }
                
            }
            //BUG: Doesn't seem to work for general chat
        }






        public override Task OnDisconnected(bool stopCalled)
        {
            //Notify all members
            ConnectionInfo cxInfo = s1.ConnectionStrings[Context.ConnectionId];
            User user = GetUserByID(cxInfo.UserID);

            if (cxInfo.ChatRoomID == Guid.Empty)
            {
                var thisMessage = new Message()
                {
                    Content = user.Name + " has left the chat room",
                    SenderID = ServerUserID,
                    TimeStamp = DateTime.Now
                };
                var listeners = s1.ConnectionStrings.Where(x => x.Value.ChatRoomID == cxInfo.ChatRoomID).Select(x => x.Key);
                foreach (var listener in listeners)
                {
                    Clients.Client(listener).NewMessage(thisMessage);
                }
            }

            //Clean up connection
            s1.ConnectionStrings.Remove(Context.ConnectionId);

            return base.OnDisconnected(stopCalled);
        }










        private Guid ParseGuid(string inputString)
        {
            Guid output;
            bool parseSuccess = Guid.TryParse(inputString, out output);
            if (!parseSuccess)
            {
                throw new HubException("Improper GUID passed.");
            }
            return output;
        }

        private User GetUserByID(Guid userID)
        {
            User user = s1.Users.FirstOrDefault(u => u.ID == userID);
            if (user == null)
            {
                throw new HubException("User does not exist");
            }

            return user;
        }

        private User GetUserByID(string userIDString)
        {
            Guid userID = ParseGuid(userIDString);
            User user = s1.Users.FirstOrDefault(u => u.ID == userID);
            if (user == null)
            {
                throw new HubException("User does not exist");
            }

            return user;
        }

        private User GetUserByConnectionID(string connectionID)
        {
            var userID = s1.ConnectionStrings[connectionID].UserID;
            var user = s1.Users.FirstOrDefault(x => x.ID == userID);
            if (user == null)
            {
                throw new HubException("User does not exist");
            }
            return user;
        }

        private ChatRoom GetChatRoom(Guid roomID)
        {
            ChatRoom chatRoom = s1.ChatRooms.FirstOrDefault(x => x.ID == roomID);
            if (chatRoom == null)
            {
                throw new HubException("Chat room does not exist");
            }
            return chatRoom;
        }

        private ChatRoom GetChatRoom(string roomIDString)
        {
            Guid roomID = ParseGuid(roomIDString);
            ChatRoom chatRoom = s1.ChatRooms.FirstOrDefault(x => x.ID == roomID);
            if (chatRoom == null)
            {
                throw new HubException("Chat room does not exist");
            }
            return chatRoom;
        }
    }
}