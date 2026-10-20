// UI vocabulary; arbitrary valid names remain supported by the string Tags API.
export const categories = [
 {name:'基础情绪',tags:['开心','悲伤','愤怒','恐惧','惊讶','兴奋','委屈','平静','冷漠']},
 {name:'复合情绪',tags:['怅然','欣慰','无奈','愧疚','释然','嫉妒','厌倦','忐忑','动情']},
 {name:'整体语调',tags:['温柔','高冷','活泼','严肃','慵懒','俏皮','深沉','干练','凌厉']},
 {name:'音色定位',tags:['磁性','醇厚','清亮','空灵','稚嫩','苍老','甜美','沙哑','醇雅']},
 {name:'人设腔调',tags:['夹子音','御姐音','正太音','大叔音','台湾腔']},
 {name:'方言',tags:['东北话','四川话','河南话','粤语']},
 {name:'角色扮演',tags:['孙悟空','林黛玉']},
 {name:'特殊风格',tags:['唱歌']}
]
export const searchStyles=(query:string)=>{
 const q=query.trim().toLowerCase()
 return categories.map(c=>({...c,tags:c.tags.filter(t=>c.name.includes(q)||t.toLowerCase().includes(q))})).filter(c=>c.tags.length)
}
export type StyleState={manual:string;selected:string[]}
export const styleText=(state:StyleState)=>[state.manual,...state.selected].filter(s=>s.length).join(' ')
export const stylePrefix=(tags:string,singing:boolean)=>(singing?'(唱歌)':'')+(tags?'('+tags+')':'')
export const isSinging=(name:string)=>/^(唱歌|sing|singing)$/i.test(name)
export function nameError(name:string){
 if(!name.length||name.length>64||name!==name.trim())return '名称须为 1–64 个 UTF-16 字符，首尾不可为空格。'
 if(/[()（）\[\]【】\p{Cc}\p{Cf}\p{Cs}]/u.test(name)||Array.from(name).some(c=>/\s/u.test(c)&&c!==' '))return '名称不可包含括号、换行、控制字符、隐藏格式字符或无效 Unicode。'
 return ''
}
export function tagsError(tags:string){
 if(tags.length>200)return '组合整体标签最多 200 个 UTF-16 字符。'
 if(/[()（）\[\]【】\p{Cc}\p{Cf}\p{Cs}]/u.test(tags)||Array.from(tags).some(c=>/\s/u.test(c)&&c!==' '))return '整体标签不可包含括号、换行、控制字符、隐藏格式字符或无效 Unicode。'
 if(tags.split(' ').some(isSinging))return '唱歌、sing、singing 请使用预置音色的独立唱歌开关。'
 return ''
}
