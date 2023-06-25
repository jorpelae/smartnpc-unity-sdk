using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Events;

namespace SmartNPC
{
    public class SmartNPCCharacter : BaseEmitter
    {
        [SerializeField] private string _characterId;
        [SerializeField] private SkinnedMeshRenderer _skinnedMeshRenderer;


        [Header("OVR Lip Sync")]
        
        [SerializeField] [Range(1, 100)] private int _visemeBlendRange = 1;
        [SerializeField] private SmartNPCVisemes _visemeBlendShapes = new SmartNPCVisemes();


        [Header("Behaviors")]
        [SerializeField] private bool _triggerGestures = true;
        [SerializeField] private List<SmartNPCExpressionConfig> _expressions = new List<SmartNPCExpressionConfig>();
        [SerializeField] public List<SmartNPCGestureAnimation> _gestures = new List<SmartNPCGestureAnimation>();
        
        
        private SmartNPCConnection _connection;
        private SmartNPCVoice _voice;
        private SmartNPCCharacterInfo _info;
        private List<SmartNPCMessage> _messages;
        private SmartNPCBehaviorQueue _behaviorQueue;
        private OVRLipSyncContext _lipSyncContext;
        private OVRLipSyncContextMorphTarget _lipSyncContextMorphTarget;
        private Animator _animator;
        private Dictionary<string, int> _blendShapeIndexes = new Dictionary<string, int>();
        private string _currentResponse = "";
        private bool _messageInProgress = false; // from message sent until message complete
        private bool _speaking = false; // from message first progress until message complete

        public readonly UnityEvent<SmartNPCMessage> OnMessageStart = new UnityEvent<SmartNPCMessage>();
        public readonly UnityEvent<SmartNPCMessage> OnMessageProgress = new UnityEvent<SmartNPCMessage>();
        public readonly UnityEvent<SmartNPCMessage> OnMessageTextComplete = new UnityEvent<SmartNPCMessage>();
        public readonly UnityEvent<SmartNPCMessage> OnMessageVoiceComplete = new UnityEvent<SmartNPCMessage>();
        public readonly UnityEvent<SmartNPCMessage> OnMessageComplete = new UnityEvent<SmartNPCMessage>();
        public readonly UnityEvent<SmartNPCMessage> OnMessageException = new UnityEvent<SmartNPCMessage>();
        public readonly UnityEvent<List<SmartNPCMessage>> OnMessageHistoryChange = new UnityEvent<List<SmartNPCMessage>>();

        void Awake()
        {
            if (_characterId == null || _characterId == "") throw new Exception("Must specify Id");

            SmartNPCConnection.OnInstanceReady(Init);
        }

        private void Init(SmartNPCConnection connection)
        {
            _connection = connection;

            _voice = GetOrAddComponent<SmartNPCVoice>();
            _behaviorQueue = GetOrAddComponent<SmartNPCBehaviorQueue>();

            InitGestures();

            if (_skinnedMeshRenderer)
            {
                MapBlendShapeIndexes();
                InitLipSync();
            }

            Action onComplete = () => {
                if (_info != null && _messages != null)
                {
                    InvokeOnUpdate(() => OnMessageHistoryChange.Invoke(_messages));

                    if (!IsReady) SetReady();
                }
            };

            FetchInfo(onComplete);
            FetchMessageHistory(onComplete);
        }

        private void InitGestures()
        {
             _animator = GetComponent<Animator>();

            // settting to a var as a workaround to avoid warning for no await
            Task applyGestureAnimationsTask = GestureAnimations.ApplyGestureAnimations(this, _gestures);

            if (_triggerGestures)
            {
                _behaviorQueue.ConsumeGestures(async (SmartNPCGesture gesture, UnityAction next) => {
                    await TriggerGesture(gesture);

                    next();
                });
            }
        }

        private void MapBlendShapeIndexes()
        {
            for (int i = 0; i < _skinnedMeshRenderer.sharedMesh.blendShapeCount; i++)
            {
                string blendShapeName = _skinnedMeshRenderer.sharedMesh.GetBlendShapeName(i);

                _blendShapeIndexes[blendShapeName] = i;
            }
        }

        private void InitLipSync()
        {
            _connection.InitLipSync();

            _lipSyncContext = GetOrAddComponent<OVRLipSyncContext>();
            _lipSyncContextMorphTarget = GetOrAddComponent<OVRLipSyncContextMorphTarget>();

            _lipSyncContext.audioLoopback = true;

            _lipSyncContextMorphTarget.skinnedMeshRenderer = _skinnedMeshRenderer;
            _lipSyncContextMorphTarget.visemeBlendRange = _visemeBlendRange;

            SetVisemeToBlendTargets();
        }

        private void SetVisemeToBlendTargets()
        {
            List<string> visemes = _visemeBlendShapes.GetBlendShapes();

            for (int i = 0; i < visemes.Count; i++)
            {
                int blendShapeIndex = _blendShapeIndexes[visemes[i]];

                if (blendShapeIndex != -1) _lipSyncContextMorphTarget.visemeToBlendTargets[i] = blendShapeIndex;
            }
        }

        public List<string> GetExpressionsBlendShapes()
        {
            HashSet<string> result = new HashSet<string>();

            _expressions.ForEach((SmartNPCExpressionConfig expression) => {
                expression.blendShapes.ForEach((SmartNPCBlendShape blendShape) => result.Add(blendShape.blendShapeName));
            });

            return new List<string>(result);
        }

        private void ResetExpression()
        {
            if (_skinnedMeshRenderer) GetExpressionsBlendShapes().ForEach((string blendShapeName) => SetBlendShapeWeight(blendShapeName, 0));
        }

        private void SetBlendShapeWeight(string name, float weight)
        {
            if (_skinnedMeshRenderer)
            {
                int index = _blendShapeIndexes[name];

                if (index != -1) _skinnedMeshRenderer.SetBlendShapeWeight(index, weight);
            }
        }

        public void SetExpression(string name)
        {
            if (!_skinnedMeshRenderer) return;

            SmartNPCExpressionConfig expression = _expressions.Find((SmartNPCExpressionConfig expression) => expression.expressionName == name);

            if (expression == null)
            {
                Debug.LogWarning("Expression not found: " + name);

                return;
            }

            ResetExpression();

            expression.blendShapes.ForEach((SmartNPCBlendShape blendShape) => SetBlendShapeWeight(blendShape.blendShapeName, blendShape.weight));
        }

        public async Task TriggerGesture(SmartNPCGesture gesture)
        {
            for (int i = 0; i < _gestures.Count; i++)
            {
                SmartNPCGestureAnimation animation = _gestures[i];

                if (animation.gestureName == gesture.name)
                {
                    if (animation.animationClip != null) await TriggerAnimation(GestureAnimations.Prefix + "-" + gesture.name + "Trigger");
                    else if (animation.animationTrigger != null && animation.animationTrigger != "") await TriggerAnimation(animation.animationTrigger);

                    break;
                }
            }
        }

        public async Task TriggerAnimation(string name)
        {
            if (_animator)
            {
                _animator.SetTrigger(name);

                await WaitUntilAnimationFinished();
            }
        }

        public async Task WaitUntilAnimationFinished()
        {
            if (_animator) await TaskUtils.WaitUntil(() => _animator.GetCurrentAnimatorStateInfo(0).normalizedTime <= 1.0f);
        }

        private void FetchInfo(Action onComplete)
        {
            _connection.Fetch<SmartNPCCharacterInfo>(new FetchOptions<SmartNPCCharacterInfo> {
               EventName = "character",
               Data = new CharacterInfoData { id = _characterId },
               OnSuccess = (response) => {
                _info = response;

                onComplete();
               },
               OnException = (response) => {
                throw new Exception("Couldn't get character info");
               }
            });
        }

        private void FetchMessageHistory(Action onComplete)
        {
            _connection.Fetch<MessageHistoryResponse>(new FetchOptions<MessageHistoryResponse> {
               EventName = "messagehistory",
               Data = new MessageHistoryData { character = _characterId },
               OnSuccess = (response) => {
                _messages = response.data.ConvertAll<SmartNPCMessage>((RawHistoryMessage rawMessage) => {
                    return new SmartNPCMessage {
                        message = rawMessage.message,
                        response = rawMessage.response,
                        behaviors = rawMessage.behaviors.ConvertAll<SmartNPCBehavior>((RawBehavior rawBehavior) => SmartNPCBehavior.parse(rawBehavior))
                    };
                });

                if (_connection.BehaviorsEnabled) InvokeOnUpdate(() => SetLastExpression(_messages));

                onComplete();
               },
               OnException = (response) => {
                throw new Exception("Couldn't get message history");
               }
            });
        }

        private void SetLastExpression(List<SmartNPCMessage> messages)
        {
            if (_messages.Count == 0) return;

            SmartNPCMessage lastMessage = _messages[_messages.Count - 1];
            List<SmartNPCBehavior> behaviors = lastMessage.behaviors;

            if (behaviors.Count == 0) return;

            SmartNPCBehavior behavior = behaviors.Find((SmartNPCBehavior behavior) => behavior.type == SmartNPCBehaviorType.Expression);

            if (behavior == null) return;
            
            SmartNPCExpression expression = behavior as SmartNPCExpression;

            SetExpression(expression.next);
        }

        public void ClearMessageHistory()
        {
            _connection.Fetch<bool>(new FetchOptions<bool> {
               EventName = "clearmessagehistory",
               Data = new MessageHistoryData { character = _characterId },
               OnSuccess = (bool value) => {
                _messages.Clear();
                
                InvokeOnUpdate(() => OnMessageHistoryChange.Invoke(_messages));
               },
               OnException = (response) => {
                throw new Exception("Couldn't clear message history");
               }
            });
        }

        public new void SendMessage(string message)
        {
            if (!_connection.IsReady) throw new Exception("Connection isn't ready");

            List<SmartNPCBehavior> behaviors = new List<SmartNPCBehavior>();

            SmartNPCExpression expression = null;

            _currentResponse = "";
            _messageInProgress = true;
            

            UnityAction<SmartNPCMessage> emitProgress = (SmartNPCMessage value) => {
                _speaking = true;

                _messages[_messages.Count - 1] = value;

                InvokeOnUpdate(() => {
                    OnMessageProgress.Invoke(value);
                    OnMessageHistoryChange.Invoke(_messages);
                });
            };

            UnityAction<MessageResponse> emitTextProgress = (MessageResponse response) => {
                SmartNPCMessage value = new SmartNPCMessage { message = message };
                
                if (response.text != "")
                {
                    _currentResponse += response.text;

                    value.chunk = response.text;
                }
                
                if (response.behavior != null)
                {
                    SmartNPCBehavior behavior = SmartNPCBehavior.parse(response.behavior);

                    if (behavior is SmartNPCExpression)
                    {
                        expression = behavior as SmartNPCExpression;

                        InvokeOnUpdate(() => SetExpression(expression.current));
                    }
                    else _behaviorQueue.Add(behavior);
                }

                value.response = _currentResponse;
                value.behaviors = behaviors;

                emitProgress(value);
            };

            UnityAction<VoiceMessage> emitVoiceProgress = (VoiceMessage response) => {
                _currentResponse += response.rawResponse.text;

                SmartNPCMessage value = new SmartNPCMessage {
                    message = message,
                    response = _currentResponse,
                    chunk = response.rawResponse.text,
                    voiceClip = response.clip
                };

                emitProgress(value);
            };
            
            if (_voice.Enabled)
            {
                _voice.Reset();

                UnityAction<VoiceMessage> onVoiceComplete = null;
                
                onVoiceComplete = (VoiceMessage response) => {
                    SmartNPCMessage value = new SmartNPCMessage { message = message, response = _currentResponse };

                    InvokeOnUpdate(() => {
                        _speaking = false;
                        _messageInProgress = false;
                        _currentResponse = "";

                        if (expression != null) SetExpression(expression.next);

                        OnMessageVoiceComplete.Invoke(value);
                        OnMessageComplete.Invoke(value);
                    });

                    _voice.OnVoiceProgress.RemoveListener(emitVoiceProgress);
                    _voice.OnVoiceComplete.RemoveListener(onVoiceComplete);
                };

                UnityAction<VoiceMessage> onPlayLastChunk = null;

                onPlayLastChunk = (VoiceMessage response) => {
                    SmartNPCMessage value = new SmartNPCMessage {
                        message = message,
                        response = _currentResponse,
                        chunk = response.rawResponse.text,
                        voiceClip = response.clip
                    };

                    InvokeOnUpdate(() => OnMessageTextComplete.Invoke(value));

                    _voice.OnPlayLastChunk.RemoveListener(onPlayLastChunk);
                };

                _voice.OnVoiceProgress.AddListener(emitVoiceProgress);
                _voice.OnVoiceComplete.AddListener(onVoiceComplete);
                _voice.OnPlayLastChunk.AddListener(onPlayLastChunk);
            }

            SmartNPCMessage newMessage = new SmartNPCMessage { message = message, response = _currentResponse, behaviors = behaviors };

            _messages.Add(newMessage);

            InvokeOnUpdate(() => {
                OnMessageStart.Invoke(newMessage);
                OnMessageHistoryChange.Invoke(_messages);
            });

            _connection.Stream(new StreamOptions<MessageResponse> {
                EventName = "message",
                Data = new MessageData {
                    character = _characterId,
                    message = message,
                    voice = _voice.Enabled,
                    behaviors = _connection.BehaviorsEnabled
                },
                OnProgress = (MessageResponse response) => {
                    if (_voice.Enabled && response.voice != null) InvokeOnUpdate(async () => await _voice.Add(response));
                    else emitTextProgress(response);
                },
                OnComplete = (MessageResponse response) => {
                    SmartNPCMessage value = new SmartNPCMessage { message = message, response = _currentResponse, behaviors = behaviors };

                    _messages[_messages.Count - 1] = value;

                    InvokeOnUpdate(() => OnMessageHistoryChange.Invoke(_messages));

                    if (_voice.Enabled) _voice.SetStreamComplete();
                    else
                    {
                        InvokeOnUpdate(() => {
                            _messageInProgress = false;
                            _speaking = false;
                            _currentResponse = "";

                            if (expression != null) SetExpression(expression.next);

                            OnMessageTextComplete.Invoke(value);
                            OnMessageComplete.Invoke(value);
                        });
                    }
                },
                OnException = (string exception) => {
                    SmartNPCMessage value = new SmartNPCMessage { message = message, exception = exception };

                    _messages[_messages.Count - 1] = value;

                    InvokeOnUpdate(() => {
                        _messageInProgress = false;
                        _speaking = false;
                        _currentResponse = "";

                        OnMessageException.Invoke(value);
                        OnMessageHistoryChange.Invoke(_messages);
                    });
                }
            });
        }

        public SmartNPCCharacterInfo Info
        {
            get { return _info; }
        }

        public List<SmartNPCMessage> Messages
        {
            get { return _messages; }
        }
        
        public SmartNPCBehaviorQueue BehaviorQueue
        {
            get { return _behaviorQueue; }
        }

        public SmartNPCConnection Connection
        {
            get { return _connection; }
        }

        public SmartNPCVoice Voice
        {
            get { return _voice; }
        }

        public bool Speaking
        {
            get { return _speaking; }
        }

        public bool MessageInProgress
        {
            get { return _messageInProgress; }
        }

        public string CurrentResponse
        {
            get { return _currentResponse; }
        }
        
        override public void Dispose()
        {
            base.Dispose();
            
            _messages.Clear();
            _behaviorQueue.Dispose();

            OnMessageStart.RemoveAllListeners();
            OnMessageProgress.RemoveAllListeners();
            OnMessageTextComplete.RemoveAllListeners();
            OnMessageVoiceComplete.RemoveAllListeners();
            OnMessageComplete.RemoveAllListeners();
            OnMessageException.RemoveAllListeners();
            OnMessageHistoryChange.RemoveAllListeners();
        }
    }
}
