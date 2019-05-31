using System;
using System.Collections;
using UnityEngine;
using UnityEngine.Networking;
using UnityEngine.UI;
using Microsoft.MixedReality.WebRTC;

namespace Microsoft.MixedReality.WebRTC.Unity
{
    /// <summary>
    /// Simple signaler for debug and testing.
    /// This is based on https://github.com/bengreenier/node-dss and SHOULD NOT BE USED FOR PRODUCTION.
    /// </summary>
    public class NodeDssSignaler : Signaler
    {
        /// <summary>
        /// Automatically log all errors to the Unity console.
        /// </summary>
        [Tooltip("Automatically log all errors to the Unity console")]
        public bool AutoLogErrors = true;

        /// <summary>
        /// Unique identifier of the local peer.
        /// </summary>
        public string LocalPeerId { get; set; }

        /// <summary>
        /// Unique identifier of the remote peer.
        /// </summary>
        [Tooltip("Unique identifier of the remote peer")]
        public string RemotePeerId;

        /// <summary>
        /// The https://github.com/bengreenier/node-dss HTTP service address to connect to
        /// </summary>
        [Header("Server")]
        [Tooltip("The node-dss server to connect to")]
        public string HttpServerAddress = "http://127.0.0.1:3000/";

        /// <summary>
        /// The interval (in ms) that the server is polled at
        /// </summary>
        [Tooltip("The interval (in ms) that the server is polled at")]
        public float PollTimeMs = 500f;

        /// <summary>
        /// Internal timing helper
        /// </summary>
        private float timeSincePollMs = 0f;
        
        /// <summary>
        /// Internal last poll response status flag
        /// </summary>
        private bool lastGetComplete = true;

        /// <summary>
        /// Unity Engine Start() hook
        /// </summary>
        /// <remarks>
        /// https://docs.unity3d.com/ScriptReference/MonoBehaviour.Start.html
        /// </remarks>
        private void Start()
        {
            if (string.IsNullOrEmpty(HttpServerAddress))
            {
                throw new ArgumentNullException("HttpServerAddress");
            }
            if (!HttpServerAddress.EndsWith("/"))
            {
                HttpServerAddress += "/";
            }

            // If not explicitly set, default local ID to some unique ID generated by Unity
            if (string.IsNullOrEmpty(LocalPeerId))
            {
                LocalPeerId = SystemInfo.deviceUniqueIdentifier;
            }
        }

        /// <summary>
        /// Callback fired when an ICE candidate message has been generated and is ready to
        /// be sent to the remote peer by the signaling object.
        /// </summary>
        /// <param name="candidate"></param>
        /// <param name="sdpMlineIndex"></param>
        /// <param name="sdpMid"></param>
        protected override void OnIceCandiateReadyToSend(string candidate, int sdpMlineIndex, string sdpMid)
        {
            StartCoroutine(PostToServer(new SignalerMessage()
            {
                MessageType = SignalerMessage.WireMessageType.Ice,
                Data = $"{candidate}|{sdpMlineIndex}|{sdpMid}",
                IceDataSeparator = "|"
            }));
        }

        /// <summary>
        /// Callback fired when a local SDP offer has been generated and is ready to
        /// be sent to the remote peer by the signaling object.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="sdp"></param>
        protected override void OnSdpOfferReadyToSend(string offer)
        {
            StartCoroutine(PostToServer(new SignalerMessage()
            {
                MessageType = SignalerMessage.WireMessageType.Offer,
                Data = offer
            }));
        }

        /// <summary>
        /// Callback fired when a local SDP answer has been generated and is ready to
        /// be sent to the remote peer by the signaling object.
        /// </summary>
        /// <param name="type"></param>
        /// <param name="sdp"></param>
        protected override void OnSdpAnswerReadyToSend(string answer)
        {
            StartCoroutine(PostToServer(new SignalerMessage()
            {
                MessageType = SignalerMessage.WireMessageType.Answer,
                Data = answer,
            }));
        }

        /// <summary>
        /// Internal helper for sending http data to the dss server using POST
        /// </summary>
        /// <param name="msg">the message to send</param>
        private IEnumerator PostToServer(SignalerMessage msg)
        {
            var data = System.Text.Encoding.UTF8.GetBytes(JsonUtility.ToJson(msg));
            var www = new UnityWebRequest($"{HttpServerAddress}data/{RemotePeerId}", UnityWebRequest.kHttpVerbPOST);
            www.uploadHandler = new UploadHandlerRaw(data);

            yield return www.SendWebRequest();

            if (AutoLogErrors && (www.isNetworkError || www.isHttpError))
            {
                Debug.Log("Failure sending message: " + www.error);
            }
        }

        /// <summary>
        /// Internal coroutine helper for receiving HTTP data from the DSS server using GET
        /// and processing it as needed
        /// </summary>
        /// <returns>the message</returns>
        private IEnumerator CO_GetAndProcessFromServer()
        {
            var www = UnityWebRequest.Get($"{HttpServerAddress}data/{LocalPeerId}");
            yield return www.SendWebRequest();

            if (!www.isNetworkError && !www.isHttpError)
            {
                var json = www.downloadHandler.text;

                var msg = JsonUtility.FromJson<SignalerMessage>(json);

                // if the message is good
                if (msg != null)
                {
                    // depending on what type of message we get, we'll handle it differently
                    // this is the "glue" that allows two peers to establish a connection.
                    Debug.Log($"Received SDP message: type={msg.MessageType} data={msg.Data}");
                    switch (msg.MessageType)
                    {
                        case SignalerMessage.WireMessageType.Offer:
                            _nativePeer.SetRemoteDescription("offer", msg.Data);
                            // if we get an offer, we immediately send an answer
                            _nativePeer.CreateAnswer();
                            break;
                        case SignalerMessage.WireMessageType.Answer:
                            _nativePeer.SetRemoteDescription("answer", msg.Data);
                            break;
                        case SignalerMessage.WireMessageType.Ice:
                            // this "parts" protocol is defined above, in PeerEventsInstance.OnIceCandiateReadyToSend listener
                            var parts = msg.Data.Split(new string[] { msg.IceDataSeparator }, StringSplitOptions.RemoveEmptyEntries);
                            _nativePeer.AddIceCandidate(parts[0], int.Parse(parts[1]), parts[2]);
                            break;
                        //case SignalerMessage.WireMessageType.SetPeer:
                        //    // this allows a remote peer to set our text target peer id
                        //    // it is primarily useful when one device does not support keyboard input
                        //    //
                        //    // note: when running this sample on HoloLens (for example) we may use postman or a similar
                        //    // tool to use this message type to set the target peer. This is NOT a production-quality solution.
                        //    TargetIdField.text = msg.Data;
                        //    break;
                        default:
                            Debug.Log("Unknown message: " + msg.MessageType + ": " + msg.Data);
                            break;
                    }
                }
                else if (AutoLogErrors)
                {
                    Debug.LogError($"Failed to deserialize JSON message : {json}");
                }
            }
            else if (AutoLogErrors && www.isNetworkError)
            {
                Debug.LogError($"Network error trying to send data to {HttpServerAddress}: {www.error}");
            }
            else
            {
                // This is very spammy because the node-dss protocol uses 404 as regular "no data yet" message, which is an HTTP error
                //Debug.LogError($"HTTP error: {www.error}");
            }

            lastGetComplete = true;
        }

        /// <summary>
        /// Unity Engine Update() hook
        /// </summary>
        /// <remarks>
        /// https://docs.unity3d.com/ScriptReference/MonoBehaviour.Update.html
        /// </remarks>
        private void Update()
        {
            // if we have not reached our PollTimeMs value...
            if (timeSincePollMs <= PollTimeMs)
            {
                // we keep incrementing our local counter until we do.
                timeSincePollMs += Time.deltaTime * 1000.0f;
                return;
            }

            // if we have a pending request still going, don't queue another yet.
            if (!lastGetComplete)
            {
                return;
            }

            // when we have reached our PollTimeMs value...
            timeSincePollMs = 0f;

            // begin the poll and process.
            lastGetComplete = false;
            StartCoroutine(CO_GetAndProcessFromServer());
        }
    }
}
